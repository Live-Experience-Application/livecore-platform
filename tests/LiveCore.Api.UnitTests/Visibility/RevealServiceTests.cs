using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="RevealService"/> (CORE-VIS-004) — the idempotent reveal command. The service
/// is driven against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), over the real visibility-rule repository and the real System
/// idempotency store, so the idempotency guarantee (the unique <c>idempotency_keys(scope, key)</c>
/// index) and the visibility state change run against genuinely persisted state.
///
/// The story's required tests are "Negative authorization tests, idempotency tests, projection
/// tests"; this suite is the IDEMPOTENCY + state-change core:
/// <list type="bullet">
///   <item>A reveal makes the resource visible: a visible rule is created when none exists, an
///   existing HIDDEN rule is flipped to visible (not duplicated), and an already-visible resource is
///   left untouched.</item>
///   <item>IDEMPOTENCY: a repeat reveal with the SAME idempotency key is recognized
///   (<see cref="RevealOutcome.AlreadyApplied"/>) and produces NO duplicate effect; a different key
///   for an already-visible resource also produces no duplicate rule.</item>
///   <item>ISOLATION: a reveal in one tenant/workspace never affects another (threat T5).</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RevealServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";

    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    // A fixed, non-empty actor (the authenticated host who executes the reveal). It is recorded as the
    // audit actor; it is intentionally NOT a seeded user profile because the audit actor column is a
    // recorded fact, not a foreign key.
    private static readonly Guid _actor = Guid.CreateVersion7();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public RevealServiceTests()
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

    private async Task SeedRuleAsync(Guid org, Guid ws, VisibilityResourceType type, Guid resourceId, VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(org, ws, type, resourceId, visibility, _now);
        await using var context = CreateContext();
        Assert.Equal(VisibilityRuleAddResult.Added, await new VisibilityRuleRepository(context).AddAsync(rule, CancellationToken.None));
    }

    private async Task<IReadOnlyList<VisibilityRule>> ListRulesAsync(Guid org, Guid ws, VisibilityResourceType type, Guid resourceId)
    {
        await using var context = CreateContext();
        return await new VisibilityRuleRepository(context)
            .ListByResourceAsync(org, ws, type, resourceId, CancellationToken.None);
    }

    private async Task<RevealResult> RevealAsync(
        Guid org,
        Guid ws,
        VisibilityResourceType type,
        Guid resourceId,
        string key,
        Guid? targetParticipantId = null)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.RevealAsync(
            org, ws, type, resourceId, targetParticipantId, _actor, key, _now, CancellationToken.None);
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
    public async Task Reveal_creates_a_visible_rule_when_the_resource_has_none()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        var result = await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Equal(RevealOutcome.Applied, result.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId);
        Assert.Single(rules);
        Assert.True(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Reveal_flips_an_existing_hidden_rule_without_creating_a_second()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);

        var result = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1");

        Assert.Equal(RevealOutcome.Applied, result.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        // The hidden rule was flipped to visible — not duplicated.
        Assert.Single(rules);
        Assert.True(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Reveal_of_an_already_visible_resource_adds_no_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);

        var result = await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-1");

        Assert.Equal(RevealOutcome.Applied, result.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        Assert.Single(rules);
        Assert.True(rules[0].IsVisibleToAudience());
    }

    // --- Idempotency -----------------------------------------------------------

    [Fact]
    public async Task Repeating_the_same_key_is_already_applied_with_no_duplicate_effect()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        var first = await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");
        var second = await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Equal(RevealOutcome.Applied, first.Outcome);
        Assert.Equal(RevealOutcome.AlreadyApplied, second.Outcome);
        // Exactly one visible rule: the retry produced no duplicate effect.
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId);
        Assert.Single(rules);
        Assert.True(rules[0].IsVisibleToAudience());
    }

    [Fact]
    public async Task Different_keys_for_an_already_visible_resource_add_no_duplicate_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        var first = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1");
        // A different idempotency key is a new request, but the resource is already visible, so
        // ensure-visible is a no-op: still one rule.
        var second = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-2");

        Assert.Equal(RevealOutcome.Applied, first.Outcome);
        Assert.Equal(RevealOutcome.Applied, second.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
    }

    // --- Selected-participant reveal (CORE-VIS-005) ----------------------------

    [Fact]
    public async Task Selected_reveal_creates_a_participant_scoped_visible_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        var result = await RevealAsync(
            org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1", participant.Id);

        Assert.Equal(RevealOutcome.Applied, result.Outcome);
        Assert.Equal(participant.Id, result.TargetParticipantId);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId);
        Assert.Single(rules);
        Assert.True(rules[0].IsVisibleToAudience());
        Assert.Equal(participant.Id, rules[0].TargetParticipantId);
        Assert.False(rules[0].IsAudienceWide);
    }

    [Fact]
    public async Task A_selected_reveal_does_not_create_an_audience_wide_rule()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);

        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        // The only rule is participant-scoped; there is NO audience-wide rule, so the whole audience
        // is not granted visibility by this private reveal.
        Assert.Single(rules);
        Assert.False(rules[0].IsAudienceWide);
    }

    [Fact]
    public async Task Audience_wide_and_selected_reveals_are_independent_dimensions()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        // An audience-wide reveal and a selected reveal of the SAME resource create two distinct rules
        // (one audience-wide, one participant-scoped) — they never collapse into one.
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-aud");
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-sel", participant.Id);

        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.IsAudienceWide && r.IsVisibleToAudience());
        Assert.Contains(rules, r => r.TargetsParticipant(participant.Id) && r.IsVisibleToAudience());
    }

    [Fact]
    public async Task Repeating_a_selected_reveal_key_is_idempotent()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        var first = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);
        var second = await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);

        Assert.Equal(RevealOutcome.Applied, first.Outcome);
        Assert.Equal(RevealOutcome.AlreadyApplied, second.Outcome);
        var rules = await ListRulesAsync(org, ws, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rules);
    }

    // --- Isolation -------------------------------------------------------------

    [Fact]
    public async Task Reveal_is_scoped_to_its_tenant_and_workspace()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, "b-show");
        var resourceId = Guid.NewGuid();

        await RevealAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId, "key-1");

        // The same resource id in tenant B's workspace is unaffected — no rule there.
        var rulesInB = await ListRulesAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId);
        Assert.Empty(rulesInB);
        var rulesInA = await ListRulesAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId);
        Assert.Single(rulesInA);
    }

    // --- Guards ----------------------------------------------------------------

    [Fact]
    public async Task Reveal_rejects_empty_ids_and_blank_key()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            Guid.Empty, ws, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, Guid.Empty, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.Empty, null, _actor, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.NewGuid(), null, _actor, "  ", _now, CancellationToken.None));
        // An explicitly empty (non-null) target participant id is rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.NewGuid(), Guid.Empty, _actor, "key", _now, CancellationToken.None));
        // An empty actor id is rejected: a visibility change must record who made it (CORE-VIS-006).
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.NewGuid(), null, Guid.Empty, "key", _now, CancellationToken.None));
    }

    [Fact]
    public async Task Reveal_rejects_an_undefined_resource_type()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RevealAsync(
            org, ws, (VisibilityResourceType)999, Guid.NewGuid(), null, _actor, "key", _now, CancellationToken.None));
    }

    // --- Audit (CORE-VIS-006) --------------------------------------------------

    [Fact]
    public async Task Reveal_records_an_audit_entry_for_the_visibility_change()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        var audit = await ListAuditAsync(org);
        var entry = Assert.Single(audit);
        Assert.Equal(AuditAction.VisibilityRuleChanged, entry.Action);
        Assert.Equal(org, entry.OrganizationId);
        Assert.Equal(ws, entry.WorkspaceId);
        Assert.Equal(_actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(VisibilityResourceType.ContentBlock), entry.ResourceType);
        Assert.Equal(resourceId, entry.ResourceId);
        Assert.Null(entry.TargetParticipantId);
        // A brand-new visible rule has no prior state.
        Assert.Null(entry.PreviousState);
        Assert.Equal(nameof(VisibilityState.Visible), entry.NewState);
    }

    [Fact]
    public async Task Reveal_of_a_hidden_rule_audits_the_hidden_to_visible_transition()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);

        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1");

        var entry = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(nameof(VisibilityState.Hidden), entry.PreviousState);
        Assert.Equal(nameof(VisibilityState.Visible), entry.NewState);
    }

    [Fact]
    public async Task Reveal_of_an_already_visible_resource_records_no_audit()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible);

        // A fresh idempotency key, but the resource is already visible: ensure-visible is a no-op, so
        // there is no change to audit.
        await RevealAsync(org, ws, VisibilityResourceType.Scene, resourceId, "key-1");

        Assert.Empty(await ListAuditAsync(org));
    }

    [Fact]
    public async Task Repeating_the_same_key_records_no_additional_audit()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");
        // The retry short-circuits before the effect, so it writes no second audit record.
        await RevealAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, "key-1");

        Assert.Single(await ListAuditAsync(org));
    }

    [Fact]
    public async Task Selected_reveal_audits_the_target_participant()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var participant = await SeedParticipantAsync(org, ws);
        var resourceId = Guid.NewGuid();

        await RevealAsync(org, ws, VisibilityResourceType.Entity, resourceId, "key-1", participant.Id);

        var entry = Assert.Single(await ListAuditAsync(org));
        Assert.Equal(participant.Id, entry.TargetParticipantId);
        Assert.Equal(nameof(VisibilityState.Visible), entry.NewState);
    }

    [Fact]
    public async Task Audit_records_are_scoped_to_their_tenant()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, "b-show");
        var resourceId = Guid.NewGuid();

        await RevealAsync(orgA, wsA, VisibilityResourceType.Entity, resourceId, "key-a");
        await RevealAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId, "key-b");

        // Each tenant's audit read returns only its own change.
        Assert.Single(await ListAuditAsync(orgA));
        var entryB = Assert.Single(await ListAuditAsync(orgB));
        Assert.Equal(orgB, entryB.OrganizationId);
        Assert.Equal(wsB, entryB.WorkspaceId);
    }
}
