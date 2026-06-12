using LiveCore.Api.Organizations;
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
        => new(new VisibilityRuleRepository(context), new IdempotencyKeyStore(context));

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

    private async Task<RevealResult> RevealAsync(Guid org, Guid ws, VisibilityResourceType type, Guid resourceId, string key)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.RevealAsync(org, ws, type, resourceId, key, _now, CancellationToken.None);
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
            Guid.Empty, ws, VisibilityResourceType.Entity, Guid.NewGuid(), "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, Guid.Empty, VisibilityResourceType.Entity, Guid.NewGuid(), "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.Empty, "key", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevealAsync(
            org, ws, VisibilityResourceType.Entity, Guid.NewGuid(), "  ", _now, CancellationToken.None));
    }

    [Fact]
    public async Task Reveal_rejects_an_undefined_resource_type()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RevealAsync(
            org, ws, (VisibilityResourceType)999, Guid.NewGuid(), "key", _now, CancellationToken.None));
    }
}
