// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="ScheduledRevealService"/> (CORE-VSEAL-002) — the background scheduled-reveal sweep. The
/// service is driven against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), over the REAL central reveal command (<see cref="RevealService"/>), the
/// real visibility-rule repository, the real idempotency store, the real audit repository, the real
/// session-event repository and the real <see cref="TransactionalUnitOfWork"/>, so the auto-reveal genuinely
/// goes through the central Visibility engine — never a duplicated reveal path.
///
/// Coverage (the story's required tests):
/// <list type="bullet">
///   <item>A rule scheduled in the PAST or AT the sweep time is auto-revealed (Hidden -&gt; Visible) and emits
///   the normal reveal session events as a SYSTEM action (no actor); a rule scheduled in the FUTURE stays
///   Hidden.</item>
///   <item>The auto-reveal goes through the central engine: a selected-participant scheduled rule reveals ONLY
///   to that participant (a non-authorized audience never receives it).</item>
///   <item>IDEMPOTENT (a rule is auto-revealed at most once): re-running the sweep does not double-reveal, and a
///   rule manually re-hidden after firing is not re-revealed (the deterministic per-rule key short-circuits).</item>
///   <item>TENANT-SAFE: each auto-reveal is driven scoped to its rule's own tenant/workspace/session.</item>
///   <item>A SEALED (locked) scheduled rule is never auto-revealed; a rule with NO schedule is never touched.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class ScheduledRevealServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _past = _now - TimeSpan.FromHours(1);
    private static readonly DateTimeOffset _future = _now + TimeSpan.FromHours(1);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ScheduledRevealServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // The sweep service and every repository/command it composes MUST share one context so the explicit
    // transaction enrols each repository's SaveChanges (mirrors the lock-service tests). The background service
    // creates one scope (one context) per sweep run; the test does the same per RunSweepAsync call.
    private static ScheduledRevealService CreateService(LiveCoreDbContext context)
        => new(
            new ScheduledRevealDueReader(context),
            new RevealService(
                new VisibilityRuleRepository(context),
                new IdempotencyKeyStore(context),
                new AuditLogRepository(context)),
            new SessionEventRepository(context),
            new TransactionalUnitOfWork(context),
            new ScheduledRevealOptions(enabled: true, TimeSpan.FromMinutes(1), batchSize: 50),
            new FixedTimeProvider(_now),
            NullLogger<ScheduledRevealService>.Instance);

    private async Task<ScheduledRevealResult> RunSweepAsync()
    {
        await using var context = CreateContext();
        return await CreateService(context).RevealDueRulesAsync(CancellationToken.None);
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId)> SeedSessionAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = "summer-show")
    {
        var organization = Organization.Create(organizationSlug, organizationSlug, _now);
        var workspace = Workspace.Create(organization.Id, workspaceSlug, workspaceSlug, _now);
        var session = Session.Create(organization.Id, workspace.Id, "Live Session", _now);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return (organization.Id, workspace.Id, session.Id);
    }

    private async Task<Participant> SeedParticipantAsync(Guid org, Guid ws)
    {
        var participant = Participant.Create(org, ws, userProfileId: null, "Participant", _now);
        await using var context = CreateContext();
        Assert.Equal(ParticipantAddResult.Added, await new ParticipantRepository(context).AddAsync(participant, CancellationToken.None));
        return participant;
    }

    private async Task<VisibilityRule> SeedRuleAsync(
        Guid org,
        Guid ws,
        Guid session,
        VisibilityResourceType type,
        DateTimeOffset? scheduledRevealAt,
        VisibilityState visibility = VisibilityState.Hidden,
        bool locked = false,
        Guid? targetParticipantId = null)
    {
        var resourceId = Guid.CreateVersion7();
        var rule = targetParticipantId is { } participantId
            ? VisibilityRule.CreateForParticipant(org, ws, session, type, resourceId, participantId, visibility, _now, scheduledRevealAt)
            : VisibilityRule.Create(org, ws, session, type, resourceId, visibility, _now, scheduledRevealAt);
        if (locked)
        {
            rule.Lock(_now);
        }

        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
        return rule;
    }

    private async Task<VisibilityRule?> LoadRuleAsync(Guid org, Guid ws, Guid session, Guid ruleId)
    {
        await using var context = CreateContext();
        return await new VisibilityRuleRepository(context).FindByIdInSessionAsync(org, ws, session, ruleId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<SessionEvent>> SessionEventsAsync(Guid org, Guid session)
    {
        await using var context = CreateContext();
        return await context.SessionEvents.AsNoTracking()
            .Where(sessionEvent => sessionEvent.OrganizationId == org && sessionEvent.SessionId == session)
            .OrderBy(sessionEvent => sessionEvent.Id)
            .ToListAsync();
    }

    private async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(Guid org)
    {
        await using var context = CreateContext();
        return await new AuditLogRepository(context).ListByOrganizationAsync(org, CancellationToken.None);
    }

    private async Task ManuallyHideAsync(Guid org, Guid ws, Guid session, Guid ruleId)
    {
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var rule = await repository.FindByIdInSessionAsync(org, ws, session, ruleId, CancellationToken.None);
        rule!.ChangeVisibility(VisibilityState.Hidden, _now);
        await repository.UpdateAsync(rule, CancellationToken.None);
    }

    // --- Past / at-now auto-reveal --------------------------------------------

    [Fact]
    public async Task A_rule_scheduled_in_the_past_is_auto_revealed()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.ContentBlock, scheduledRevealAt: _past);

        var result = await RunSweepAsync();

        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Revealed);
        Assert.Equal(0, result.AlreadyApplied);
        Assert.Equal(0, result.Failed);
        var revealed = await LoadRuleAsync(org, ws, session, rule.Id);
        Assert.True(revealed!.IsVisibleToAudience());
    }

    [Fact]
    public async Task A_rule_scheduled_at_the_sweep_time_is_auto_revealed()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: _now);

        var result = await RunSweepAsync();

        Assert.Equal(1, result.Revealed);
        Assert.True((await LoadRuleAsync(org, ws, session, rule.Id))!.IsVisibleToAudience());
    }

    [Fact]
    public async Task An_auto_reveal_emits_the_normal_reveal_events_as_a_system_action()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.ContentBlock, scheduledRevealAt: _past);

        await RunSweepAsync();

        // The same events a live reveal emits — ContentRevealed + VisibilityRuleChanged — appended to the
        // session's stream, as a SYSTEM event (no actor), carrying the revealed resource as the visibility
        // subject so the recipient resolver gates delivery to the authorized audience only.
        var events = await SessionEventsAsync(org, session);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.VisibilityRuleChanged);
        Assert.All(events, e => Assert.Null(e.CreatedBy));
        var contentRevealed = events.Single(e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Equal(nameof(VisibilityResourceType.ContentBlock), contentRevealed.VisibilitySubjectType);
        Assert.Equal(rule.ResourceId, contentRevealed.VisibilitySubjectId);
        Assert.Null(contentRevealed.TargetParticipantId);

        // The audit fact for the visibility change is recorded as a SYSTEM action (no actor).
        var audit = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(AuditAction.VisibilityRuleChanged, audit.Action);
        Assert.Null(audit.ActorUserProfileId);
        Assert.Equal(nameof(VisibilityState.Hidden), audit.PreviousState);
        Assert.Equal(nameof(VisibilityState.Visible), audit.NewState);
    }

    [Fact]
    public async Task Auto_revealing_a_scene_also_emits_scene_activated()
    {
        var (org, ws, session) = await SeedSessionAsync();
        await SeedRuleAsync(org, ws, session, VisibilityResourceType.Scene, scheduledRevealAt: _past);

        await RunSweepAsync();

        var events = await SessionEventsAsync(org, session);
        Assert.Contains(events, e => e.EventType == SessionEventTypes.SceneActivated);
    }

    // --- Future stays hidden ---------------------------------------------------

    [Fact]
    public async Task A_rule_scheduled_in_the_future_stays_hidden()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: _future);

        var result = await RunSweepAsync();

        // Not yet due: it is not even examined, stays hidden, emits nothing.
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Revealed);
        Assert.False((await LoadRuleAsync(org, ws, session, rule.Id))!.IsVisibleToAudience());
        Assert.Empty(await SessionEventsAsync(org, session));
    }

    [Fact]
    public async Task A_rule_with_no_schedule_is_never_auto_revealed()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: null);

        var result = await RunSweepAsync();

        Assert.Equal(0, result.Examined);
        Assert.False((await LoadRuleAsync(org, ws, session, rule.Id))!.IsVisibleToAudience());
    }

    // --- Central engine: selected-participant reaches only that participant ----

    [Fact]
    public async Task A_selected_participant_scheduled_reveal_reaches_only_that_participant()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var target = await SeedParticipantAsync(org, ws);
        var other = await SeedParticipantAsync(org, ws);
        var rule = await SeedRuleAsync(
            org, ws, session, VisibilityResourceType.ContentBlock, scheduledRevealAt: _past, targetParticipantId: target.Id);

        await RunSweepAsync();

        // The reveal event targets ONLY the selected participant.
        var contentRevealed = (await SessionEventsAsync(org, session)).Single(e => e.EventType == SessionEventTypes.ContentRevealed);
        Assert.Equal(target.Id, contentRevealed.TargetParticipantId);

        // The central Visibility engine confirms: the target participant may see it; a different participant may
        // NOT (a non-authorized audience never receives it; threat T5).
        await using var context = CreateContext();
        var policy = new VisibilityPolicy(new VisibilityRuleRepository(context));
        Assert.True(await policy.CanParticipantViewResourceAsync(
            org, ws, session, target.Id, VisibilityResourceType.ContentBlock, rule.ResourceId, CancellationToken.None));
        Assert.False(await policy.CanParticipantViewResourceAsync(
            org, ws, session, other.Id, VisibilityResourceType.ContentBlock, rule.ResourceId, CancellationToken.None));
    }

    // --- Idempotency: a rule is auto-revealed at most once ---------------------

    [Fact]
    public async Task Re_running_the_sweep_does_not_double_reveal()
    {
        var (org, ws, session) = await SeedSessionAsync();
        await SeedRuleAsync(org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: _past);

        var first = await RunSweepAsync();
        var second = await RunSweepAsync();

        Assert.Equal(1, first.Revealed);
        // After the first sweep the rule is Visible, so it is no longer due — the second sweep examines nothing.
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Revealed);
        // Exactly one set of reveal events and exactly one audit fact.
        Assert.Single(await ListAuditAsync(org));
        Assert.Equal(2, (await SessionEventsAsync(org, session)).Count(e =>
            e.EventType is SessionEventTypes.ContentRevealed or SessionEventTypes.VisibilityRuleChanged));
    }

    [Fact]
    public async Task A_rule_manually_hidden_after_firing_is_not_re_revealed()
    {
        // The deterministic per-rule reveal idempotency key is the at-most-once backstop: even if the rule is
        // manually hidden again (so it becomes due once more), the auto-reveal short-circuits as an idempotent
        // retry and never re-reveals or re-emits.
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: _past);

        await RunSweepAsync();
        await ManuallyHideAsync(org, ws, session, rule.Id);

        var rerun = await RunSweepAsync();

        // It is examined again (Hidden + still due) but NOT re-revealed — the key short-circuits it.
        Assert.Equal(1, rerun.Examined);
        Assert.Equal(0, rerun.Revealed);
        Assert.Equal(1, rerun.AlreadyApplied);
        Assert.False((await LoadRuleAsync(org, ws, session, rule.Id))!.IsVisibleToAudience());
        // No second set of reveal events from the re-run.
        Assert.Equal(2, (await SessionEventsAsync(org, session)).Count(e =>
            e.EventType is SessionEventTypes.ContentRevealed or SessionEventTypes.VisibilityRuleChanged));
    }

    // --- Sealed (locked) scheduled rule is never auto-revealed -----------------

    [Fact]
    public async Task A_locked_scheduled_rule_is_not_auto_revealed()
    {
        var (org, ws, session) = await SeedSessionAsync();
        var rule = await SeedRuleAsync(
            org, ws, session, VisibilityResourceType.Entity, scheduledRevealAt: _past, locked: true);

        var result = await RunSweepAsync();

        // The due read excludes sealed rules (their visibility can never change), so it is never examined.
        Assert.Equal(0, result.Examined);
        var loaded = await LoadRuleAsync(org, ws, session, rule.Id);
        Assert.False(loaded!.IsVisibleToAudience());
        Assert.True(loaded.Locked);
        Assert.Empty(await SessionEventsAsync(org, session));
    }

    // --- Tenant safety ---------------------------------------------------------

    [Fact]
    public async Task Each_due_rule_is_auto_revealed_scoped_to_its_own_tenant()
    {
        var (orgA, wsA, sessionA) = await SeedSessionAsync(_organizationSlugA, "a-show");
        var (orgB, wsB, sessionB) = await SeedSessionAsync(_organizationSlugB, "b-show");
        var ruleA = await SeedRuleAsync(orgA, wsA, sessionA, VisibilityResourceType.Entity, scheduledRevealAt: _past);
        var ruleB = await SeedRuleAsync(orgB, wsB, sessionB, VisibilityResourceType.Entity, scheduledRevealAt: _past);

        var result = await RunSweepAsync();

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, result.Revealed);
        // Each rule is revealed within its OWN tenant, and each tenant's events/audit are scoped to it.
        Assert.True((await LoadRuleAsync(orgA, wsA, sessionA, ruleA.Id))!.IsVisibleToAudience());
        Assert.True((await LoadRuleAsync(orgB, wsB, sessionB, ruleB.Id))!.IsVisibleToAudience());
        Assert.Single(await ListAuditAsync(orgA));
        Assert.Single(await ListAuditAsync(orgB));
        Assert.NotEmpty(await SessionEventsAsync(orgA, sessionA));
        Assert.NotEmpty(await SessionEventsAsync(orgB, sessionB));
    }

    // --- Disabled options ------------------------------------------------------

    [Fact]
    public void Options_are_off_by_default()
    {
        // Server-enforced scheduled reveal is opt-in per deployment: with no configuration the sweep is disabled,
        // so the worker registers no loop (fail-safe).
        var options = ScheduledRevealOptions.FromConfiguration(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        Assert.Equal(ScheduledRevealOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(ScheduledRevealOptions.DefaultBatchSize, options.BatchSize);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
