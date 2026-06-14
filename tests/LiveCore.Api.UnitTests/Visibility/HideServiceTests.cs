using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="RevealService.HideAsync"/> (CORE-REV-001, the "Reveal Lifecycle" hide /
/// un-reveal) — the idempotent hide command, the inverse of the reveal command. The service is driven
/// against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// over the real visibility-rule repository, the real System idempotency store and the real audit
/// repository, so the idempotency guarantee (the unique <c>idempotency_keys(scope, key)</c> index), the
/// visibility state change and the audit record run against genuinely persisted state.
///
/// The story's required tests are "reveal then hide removes visibility for audience and selected
/// participant; negative role/tenant tests; idempotent double-hide"; this suite is the SERVICE-level
/// IDEMPOTENCY + state-change + audit core (the HTTP authorization/tenant negatives live in
/// <c>HideEndpointTests</c>):
/// <list type="bullet">
///   <item>A hide makes the resource hidden: a visible rule is flipped to hidden, an already-hidden rule
///   is left untouched, and a resource with no rule needs no rule (absence already means hidden).</item>
///   <item>REVEAL THEN HIDE removes visibility for the audience and for a selected participant.</item>
///   <item>IDEMPOTENCY: a repeat hide with the SAME key is recognized
///   (<see cref="HideOutcome.AlreadyApplied"/>) and produces NO duplicate effect; the reveal and hide
///   scopes are independent, so the same key value pairs a reveal with its hide.</item>
///   <item>AUDIT: a real Visible -> Hidden change is audited; a no-op hide audits nothing.</item>
///   <item>ISOLATION: a hide in one tenant/workspace never affects another (threat T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class HideServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";

    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    // A fixed, non-empty actor (the authenticated host who executes the hide). It is recorded as the
    // audit actor; it is intentionally NOT a seeded user profile because the audit actor column is a
    // recorded fact, not a foreign key.
    private static readonly Guid _actor = Guid.CreateVersion7();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    public HideServiceTests()
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

    private RevealService CreateService(LiveCoreDbContext context)
        => new(
            new VisibilityRuleRepository(context),
            new IdempotencyKeyStore(context),
            new AuditLogRepository(context));

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _now);
        await using var context = CreateContext();
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization.Id, workspace.Id);
    }

    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var session = Session.Create(organizationId, workspaceId, "Live Session", _now);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        _sessionByWorkspace[workspaceId] = session.Id;
        return session.Id;
    }

    private async Task SeedRuleAsync(Guid org, Guid ws, VisibilityResourceType type, Guid resourceId, VisibilityState visibility)
    {
        var sessionId = await SessionIdAsync(org, ws);
        var rule = VisibilityRule.Create(org, ws, sessionId, type, resourceId, visibility, _now);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    private async Task<IReadOnlyList<VisibilityRule>> ListRulesAsync(Guid org, Guid ws, VisibilityResourceType type, Guid resourceId)
    {
        var sessionId = await SessionIdAsync(org, ws);
        await using var context = CreateContext();
        return await new VisibilityRuleRepository(context)
            .ListByResourceAsync(org, ws, sessionId, type, resourceId, CancellationToken.None);
    }

    private async Task<RevealResult> RevealAsync(
        Guid org,
        Guid ws,
        VisibilityResourceType type,
        Guid resourceId,
        string key,
        Guid? targetParticipantId = null)
    {
        var sessionId = await SessionIdAsync(org, ws);
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.RevealAsync(
            org, ws, sessionId, type, resourceId, targetParticipantId, _actor, key, _now, CancellationToken.None);
    }

    private async Task<HideResult> HideAsync(
        Guid org,
        Guid ws,
        VisibilityResourceType type,
        Guid resourceId,
        string key,
        Guid? targetParticipantId = null)
    {
        var sessionId = await SessionIdAsync(org, ws);
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.HideAsync(
            org, ws, sessionId, type, resourceId, targetParticipantId, _actor, key, _now, CancellationToken.None);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> ListAuditAsync(Guid org)
    {
        await using var context = CreateContext();
        return await new AuditLogRepository(context).ListByOrganizationAsync(org, CancellationToken.None);
    }

    private async Task<Participant> SeedParticipantAsync(Guid org, Guid ws)
    {
        var participant = Participant.Create(org, ws, userProfileId: null, "Participant", _now);
        await using var context = CreateContext();
        Assert.Equal(ParticipantAddResult.Added, await new ParticipantRepository(context).AddAsync(participant, CancellationToken.None));
        return participant;
    }

    // --- State change ----------------------------------------------------------

    [Fact]
    public async Task Hide_flips_an_existing_visible_rule_to_hidden_without_creating_a_second()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        var result = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1");

        Assert.Equal(HideOutcome.Applied, result.Outcome);
        // A real change: the visible rule was flipped to hidden (the realtime event signal).
        Assert.True(result.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        // The visible rule was flipped to hidden — not duplicated.
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Hide_of_a_resource_with_no_rule_creates_no_rule_and_is_a_no_op()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        var result = await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Equal(HideOutcome.Applied, result.Outcome);
        // Absence already means hidden: no rule is created and nothing changed.
        Assert.False(result.VisibilityChanged);
        Assert.Empty(await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId));
    }

    [Fact]
    public async Task Hide_of_an_already_hidden_rule_changes_nothing()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Hidden);

        var result = await HideAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-1");

        Assert.Equal(HideOutcome.Applied, result.Outcome);
        // Already hidden: no change, so no realtime event is emitted.
        Assert.False(result.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    // --- Reveal then hide (the acceptance scenario) ----------------------------

    [Fact]
    public async Task Reveal_then_hide_removes_audience_wide_visibility()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-reveal");
        var hide = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-hide");

        Assert.Equal(HideOutcome.Applied, hide.Outcome);
        Assert.True(hide.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        // The single audience-wide rule is now hidden, so the audience no longer sees it.
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Reveal_then_hide_removes_visibility_for_the_selected_participant_only()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var selected = await SeedParticipantAsync(org, ws);
        var other = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-reveal", selected.Id);
        var hide = await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-hide", selected.Id);

        Assert.Equal(HideOutcome.Applied, hide.Outcome);
        Assert.True(hide.VisibilityChanged);
        Assert.Equal(selected.Id, hide.TargetParticipantId);

        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId);
        // The participant-scoped rule is now hidden; neither participant can see it through it.
        Assert.Single(rules);
        Assert.DoesNotContain(rules, rule => rule.IsVisibleTo(selected.Id));
        Assert.DoesNotContain(rules, rule => rule.IsVisibleTo(other.Id));
    }

    [Fact]
    public async Task An_audience_wide_hide_does_not_affect_a_selected_participants_private_reveal()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var selected = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        // Reveal audience-wide AND to a selected participant (two independent dimensions), then hide
        // ONLY the audience-wide dimension.
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-aud");
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-sel", selected.Id);
        await HideAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-hide-aud");

        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        Assert.Equal(2, rules.Count);
        // The audience-wide rule is hidden; the participant-scoped rule is untouched, so the selected
        // participant still sees the resource.
        Assert.Contains(rules, rule => rule.IsVisibleTo(selected.Id));
        Assert.DoesNotContain(rules, rule => rule.IsAudienceWide && rule.IsVisibleToAudience());
    }

    [Fact]
    public async Task A_selected_hide_does_not_affect_the_audience_wide_reveal()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var selected = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-aud");
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-sel", selected.Id);
        // Hide ONLY the participant dimension.
        await HideAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-hide-sel", selected.Id);

        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        Assert.Equal(2, rules.Count);
        // The audience-wide rule is still visible (it survives the selected hide).
        Assert.Contains(rules, rule => rule.IsAudienceWide && rule.IsVisibleToAudience());
    }

    // --- Ghost-reveal prevention (CORE-SVIS-002) -------------------------------

    [Fact]
    public async Task Reveal_then_hide_leaves_the_resource_hidden_even_under_a_racing_second_reveal()
    {
        // The headline CORE-SVIS-002 scenario: two first-reveals race, then a hide. Before the fix the two
        // reveals created TWO visible rules and the hide flipped only one, leaving the other Visible as an
        // un-hideable ghost. Now the unique index lets only ONE rule exist, so the hide fully reverses it —
        // no resource stays visible after a successful hide.
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionId = await SessionIdAsync(org, ws);
        var resourceId = Guid.NewGuid();

        // Drive the SECOND reveal through a race-injecting repository: just before it inserts, the first
        // reveal commits its rule, so the second loses the create race and converges onto the one rule.
        async Task InjectWinningRevealAsync()
        {
            await using var winnerContext = CreateContext();
            var winner = VisibilityRule.Create(
                org, ws, sessionId, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible, _now);
            Assert.Equal(
                VisibilityRuleAddResult.Added,
                await new VisibilityRuleRepository(winnerContext).AddAsync(winner, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var racingRules = new RaceInjectingVisibilityRuleRepository(
                new VisibilityRuleRepository(context), InjectWinningRevealAsync);
            var service = new RevealService(racingRules, new IdempotencyKeyStore(context), new AuditLogRepository(context));
            await service.RevealAsync(
                org, ws, sessionId, VisibilityResourceType.Entity, resourceId, null, _actor, "key-loser", _now, CancellationToken.None);
        }

        // Exactly one rule survived the racing first-reveals.
        Assert.Single(await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId));

        // The hide flips the one rule to hidden; with no second (ghost) rule, NO rule stays visible.
        var hide = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-hide");
        Assert.Equal(HideOutcome.Applied, hide.Outcome);
        Assert.True(hide.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task A_second_hide_with_a_different_key_is_a_no_op_and_leaves_the_resource_hidden()
    {
        // Idempotent double-hide across DIFFERENT keys (state-level idempotence, not just the key
        // short-circuit): once hidden, a second hide changes nothing and the resource stays hidden.
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        var first = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-hide-1");
        var second = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-hide-2");

        // The first hide changed visibility (emit the event); the second was a no-op (already hidden).
        Assert.Equal(HideOutcome.Applied, first.Outcome);
        Assert.True(first.VisibilityChanged);
        Assert.Equal(HideOutcome.Applied, second.Outcome);
        Assert.False(second.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
        // The no-op second hide audited nothing: only the first hide's real change is recorded.
        Assert.Single(await ListAuditAsync(org));
    }

    // --- Idempotency -----------------------------------------------------------

    [Fact]
    public async Task Repeating_the_same_hide_key_is_already_applied_with_no_second_effect()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        var first = await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");
        var second = await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Equal(HideOutcome.Applied, first.Outcome);
        Assert.Equal(HideOutcome.AlreadyApplied, second.Outcome);
        // The first call changed visibility (emit the event); the retry did not (emit nothing).
        Assert.True(first.VisibilityChanged);
        Assert.False(second.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task The_reveal_and_hide_idempotency_scopes_are_independent()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        // The SAME key value drives a reveal and then a hide: because the scopes differ
        // (reveal:{org} vs hide:{org}), the hide is NOT short-circuited by the reveal's key.
        var reveal = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "shared-key");
        var hide = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "shared-key");

        Assert.Equal(RevealOutcome.Applied, reveal.Outcome);
        Assert.Equal(HideOutcome.Applied, hide.Outcome);
        Assert.True(reveal.VisibilityChanged);
        Assert.True(hide.VisibilityChanged);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Repeating_a_selected_hide_key_is_idempotent()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();
        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-reveal", participant.Id);

        var first = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);
        var second = await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);

        Assert.Equal(HideOutcome.Applied, first.Outcome);
        Assert.Equal(HideOutcome.AlreadyApplied, second.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
        Assert.False(rules[0].IsVisibleToAudience());
    }

    // --- Isolation -------------------------------------------------------------

    [Fact]
    public async Task Hide_is_scoped_to_its_tenant_and_workspace()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, "b-show");
        var resourceId = Guid.NewGuid();
        // Both tenants have the same resource id revealed (visible).
        await SeedRuleAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);
        await SeedRuleAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        await HideAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId, "key-1");

        // Tenant B's rule is unaffected — still visible.
        var rulesInB = await ListRulesAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId);
        Assert.True(rulesInB[0].IsVisibleToAudience());
        var rulesInA = await ListRulesAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId);
        Assert.False(rulesInA[0].IsVisibleToAudience());
    }

    // --- Guards ----------------------------------------------------------------

    [Fact]
    public async Task Hide_rejects_empty_ids_and_blank_key()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionId = await SessionIdAsync(org, ws);
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            Guid.Empty, ws, sessionId, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            org, Guid.Empty, sessionId, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            org, ws, sessionId, VisibilityResourceType.Entity, Guid.Empty, null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            org, ws, sessionId, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "  ", _now, CancellationToken.None));
        // An explicitly empty (non-null) target participant id is rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            org, ws, sessionId, VisibilityResourceType.Entity, Guid.NewGuid(), Guid.Empty, _actor, "key", _now, CancellationToken.None));
        // An empty actor id is rejected: a visibility change must record who made it (CORE-VIS-006).
        await Assert.ThrowsAsync<ArgumentException>(() => service.HideAsync(
            org, ws, sessionId, VisibilityResourceType.Entity, Guid.NewGuid(), null, Guid.Empty, "key", _now, CancellationToken.None));
    }

    [Fact]
    public async Task Hide_rejects_an_undefined_resource_type()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionId = await SessionIdAsync(org, ws);
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.HideAsync(
            org, ws, sessionId, (VisibilityResourceType)999, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
    }

    // --- Audit (CORE-VIS-006) --------------------------------------------------

    [Fact]
    public async Task Hide_records_an_audit_entry_for_the_visible_to_hidden_change()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        var entry = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(AuditAction.VisibilityRuleChanged, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(_actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(VisibilityResourceType.ContentBlock), entry.ResourceType);
        Assert.Equal(resourceId, entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), entry.PreviousState);
        Assert.Equal(nameof(VisibilityState.Hidden), entry.NewState);
    }

    [Fact]
    public async Task Hide_of_a_resource_with_no_rule_records_no_audit()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        // A fresh idempotency key, but the resource has no rule (already hidden): no change to audit.
        await HideAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-1");

        Assert.Empty(await ListAuditAsync(org));
    }

    [Fact]
    public async Task Repeating_the_same_hide_key_records_no_additional_audit()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");
        // The retry short-circuits before the effect, so it writes no second audit record.
        await HideAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Single(await ListAuditAsync(org));
    }

    [Fact]
    public async Task Selected_hide_audits_the_target_participant()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();
        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-reveal", participant.Id);

        await HideAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-hide", participant.Id);

        // Two audit entries: the reveal (NewState Visible) then the hide (NewState Hidden). Select the
        // HIDE entry by its resulting state rather than by list position — the reveal and hide here share
        // the same command timestamp (_now), so their time-ordered surrogate ids tie-break on UUIDv7's
        // random bits and can read back in either order (a position-based pick was flaky).
        var audit = await ListAuditAsync(org);
        var entry = Assert.Single(audit, e => e.NewState == nameof(VisibilityState.Hidden));
        Assert.Equal(participant.Id, entry.TargetParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), entry.PreviousState);
        Assert.Equal(nameof(VisibilityState.Hidden), entry.NewState);
    }
}
