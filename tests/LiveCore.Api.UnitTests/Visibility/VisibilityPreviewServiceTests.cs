using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Tests for <see cref="VisibilityPreviewService"/> (CORE-VIS-003) — the preview-as-participant query
/// (<c>GetVisibleResourcesForParticipant</c>). The service computes the set of resources an audience
/// participant may see in a workspace by routing every candidate resource through the CORE-VIS-002
/// <see cref="VisibilityPolicy"/>, driven against an in-memory SQLite database with foreign keys
/// enforced (<c>PRAGMA foreign_keys = ON</c>), so the tenant/workspace-scoped rule reads run against
/// genuinely persisted rules — exactly like the CORE-VIS-002 policy tests.
///
/// The story's required tests are "Negative authorization tests, idempotency tests, projection
/// tests"; this suite covers:
/// <list type="bullet">
///   <item>PROJECTION (the audience viewpoint): only resources made VISIBLE by a rule appear; hidden
///   and rule-less resources do not — the participant-safe projection of what an audience member
///   sees.</item>
///   <item>NEGATIVE / ISOLATION (threat T5): a visible rule in another WORKSPACE or TENANT never
///   contributes to this workspace's visible set; a hidden-only resource is never visible.</item>
///   <item>Deterministic, DISTINCT output: a resource with several visible rules appears once; the
///   order is stable (by resource type then id).</item>
/// </list>
/// "idempotency tests" target the later reveal command (CORE-VIS-004); this is a read-only query (and
/// is naturally repeatable — a second call returns the same set).
///
/// All fixtures are GENERIC — no vertical vocabulary appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class VisibilityPreviewServiceTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public VisibilityPreviewServiceTests()
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

    private VisibilityPreviewService CreateService(LiveCoreDbContext context)
    {
        var repository = new VisibilityRuleRepository(context);
        return new VisibilityPreviewService(repository, new VisibilityPolicy(repository));
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task SeedRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        VisibilityState visibility)
    {
        var rule = VisibilityRule.Create(organizationId, workspaceId, resourceType, resourceId, visibility, _createdAt);
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        Assert.Equal(VisibilityRuleAddResult.Added, await repository.AddAsync(rule, CancellationToken.None));
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization.Id, workspace.Id);
    }

    private async Task<IReadOnlyList<VisibleResource>> GetVisibleAsync(Guid org, Guid ws)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        return await service.GetVisibleResourcesForParticipantAsync(org, ws, CancellationToken.None);
    }

    [Fact]
    public async Task Empty_workspace_has_no_visible_resources()
    {
        var (org, ws) = await SeedWorkspaceAsync();

        Assert.Empty(await GetVisibleAsync(org, ws));
    }

    [Fact]
    public async Task Hidden_rules_alone_yield_no_visible_resources()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, Guid.NewGuid(), VisibilityState.Hidden);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, Guid.NewGuid(), VisibilityState.Hidden);

        Assert.Empty(await GetVisibleAsync(org, ws));
    }

    [Fact]
    public async Task A_visible_rule_makes_its_resource_visible()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, resourceId, VisibilityState.Visible);

        var visible = await GetVisibleAsync(org, ws);

        Assert.Equal(new[] { new VisibleResource(VisibilityResourceType.ContentBlock, resourceId) }, visible);
    }

    [Fact]
    public async Task Only_visible_resources_are_returned_from_a_mixed_workspace()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var visibleScene = Guid.NewGuid();
        var visibleEntity = Guid.NewGuid();
        var hiddenContent = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, visibleScene, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, visibleEntity, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, hiddenContent, VisibilityState.Hidden);

        var visible = await GetVisibleAsync(org, ws);

        Assert.Equal(2, visible.Count);
        Assert.Contains(new VisibleResource(VisibilityResourceType.Scene, visibleScene), visible);
        Assert.Contains(new VisibleResource(VisibilityResourceType.Entity, visibleEntity), visible);
        Assert.DoesNotContain(new VisibleResource(VisibilityResourceType.ContentBlock, hiddenContent), visible);
    }

    [Fact]
    public async Task A_resource_with_several_rules_appears_once_if_any_is_visible()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // Same resource, three rules (the non-unique index permits this): two hidden, one visible.
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);

        var visible = await GetVisibleAsync(org, ws);

        Assert.Equal(new[] { new VisibleResource(VisibilityResourceType.Entity, resourceId) }, visible);
    }

    [Fact]
    public async Task Same_id_different_resource_types_are_distinct_resources()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var sharedId = Guid.NewGuid();
        // The SAME surrogate id used for a Scene (visible) and an Entity (hidden): they are different
        // resources, so only the visible one is returned.
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, sharedId, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, sharedId, VisibilityState.Hidden);

        var visible = await GetVisibleAsync(org, ws);

        Assert.Equal(new[] { new VisibleResource(VisibilityResourceType.Scene, sharedId) }, visible);
    }

    [Fact]
    public async Task Visible_resources_are_returned_in_deterministic_order()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var scene = Guid.NewGuid();
        var content = Guid.NewGuid();
        var entity = Guid.NewGuid();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, entity, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, scene, VisibilityState.Visible);
        await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, content, VisibilityState.Visible);

        var visible = await GetVisibleAsync(org, ws);

        // Deterministic order: by resource type (Scene=1, ContentBlock=2, Entity=3) then id.
        var expected = new[]
        {
            new VisibleResource(VisibilityResourceType.Scene, scene),
            new VisibleResource(VisibilityResourceType.ContentBlock, content),
            new VisibleResource(VisibilityResourceType.Entity, entity),
        };
        Assert.Equal(expected, visible);
    }

    [Fact]
    public async Task A_visible_rule_in_another_workspace_is_not_included()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(organization.Id, workspaceB.Id, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        // Workspace A's visible set must not borrow workspace B's visible rule.
        Assert.Empty(await GetVisibleAsync(organization.Id, workspaceA.Id));
    }

    [Fact]
    public async Task A_visible_rule_in_another_tenant_is_not_included()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, wsB) = await SeedWorkspaceAsync(_organizationSlugB, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(orgB, wsB, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);

        // Tenant A's visible set must not borrow tenant B's visible rule (organization boundary before
        // workspace boundary; threat T5).
        Assert.Empty(await GetVisibleAsync(orgA, wsA));
    }

    [Fact]
    public async Task The_query_is_repeatable()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, Guid.NewGuid(), VisibilityState.Visible);

        var first = await GetVisibleAsync(org, ws);
        var second = await GetVisibleAsync(org, ws);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Empty_ids_are_rejected()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetVisibleResourcesForParticipantAsync(
            Guid.Empty, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetVisibleResourcesForParticipantAsync(
            Guid.NewGuid(), Guid.Empty, CancellationToken.None));
    }
}
