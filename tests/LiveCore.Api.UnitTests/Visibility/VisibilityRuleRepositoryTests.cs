using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Visibility;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="VisibilityRuleRepository"/>
/// (CORE-VIS-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the foreign keys
/// into <c>organizations</c>/<c>workspaces</c>, the enum-by-name round-trip and the
/// <see cref="VisibilityRule.ChangeVisibility"/> update are exercised on every test run without any
/// database server. The behaviors under test are relational semantics shared with PostgreSQL;
/// provider-specific verification happens against PostgreSQL in the deployment pipeline.
///
/// The story's required tests are "Negative authorization tests, idempotency tests, projection
/// tests". At the aggregate + repository level (no policy, no command, no endpoint yet):
/// <list type="bullet">
///   <item>NEGATIVE AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1; docs/06_AUTHORIZATION_MATRIX.md: organization
///   boundary checked before workspace boundary): a rule in workspace W1 is never returned via
///   workspace W2; a rule in organization A is never resolved via organization B (both directions);
///   ListByWorkspace and ListByResource never borrow another workspace's or tenant's rules; and the
///   foreign keys reject a rule referencing a non-existent organization or workspace.</item>
///   <item>The POLYMORPHIC resource reference: <c>resource_id</c> is NOT a foreign key, so a rule
///   may reference any resource id; the same-workspace coupling is the application layer's
///   responsibility. One test documents this precisely.</item>
/// </list>
/// "idempotency tests" and "projection tests" legitimately target later stories — the reveal command
/// (CORE-VIS-004) owns idempotency, and host/participant projection (CORE-VIS-002/003) owns
/// projection — so they do not apply to this bare rule model.
///
/// All fixtures are GENERIC and NEUTRAL — no vertical vocabulary appears (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public sealed class VisibilityRuleRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public VisibilityRuleRepositoryTests()
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

    private readonly Dictionary<Guid, Guid> _sessionByWorkspace = new();

    private async Task<Session> SeedSessionAsync(Guid organizationId, Guid workspaceId, string title = "Live Session")
    {
        var session = Session.Create(organizationId, workspaceId, title, _createdAt);
        await using var context = CreateContext();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    /// <summary>Lazily seeds and caches ONE session per workspace, so rules sharing a workspace share a session.</summary>
    private async Task<Guid> SessionIdAsync(Guid organizationId, Guid workspaceId)
    {
        if (_sessionByWorkspace.TryGetValue(workspaceId, out var existing))
        {
            return existing;
        }

        var session = await SeedSessionAsync(organizationId, workspaceId);
        _sessionByWorkspace[workspaceId] = session.Id;
        return session.Id;
    }

    private async Task<VisibilityRule> SeedRuleAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType = VisibilityResourceType.ContentBlock,
        Guid? resourceId = null,
        VisibilityState visibility = VisibilityState.Hidden,
        Guid? sessionId = null)
    {
        var session = sessionId ?? await SessionIdAsync(organizationId, workspaceId);
        var rule = VisibilityRule.Create(
            organizationId, workspaceId, session, resourceType, resourceId ?? Guid.NewGuid(), visibility, _createdAt);
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        Assert.Equal(VisibilityRuleAddResult.Added, await repository.AddAsync(rule, CancellationToken.None));
        return rule;
    }

    private async Task<(Guid OrganizationId, Guid WorkspaceId)> SeedWorkspaceAsync(
        string organizationSlug = _organizationSlugA,
        string workspaceSlug = _workspaceSlugA)
    {
        var organization = await SeedOrganizationAsync(organizationSlug);
        var workspace = await SeedWorkspaceAsync(organization.Id, workspaceSlug);
        return (organization.Id, workspace.Id);
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task Rule_round_trips_through_the_database()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        var seeded = await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Hidden);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var loaded = await repository.FindByIdAsync(org, ws, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(org, loaded.OrganizationId);
        Assert.Equal(ws, loaded.WorkspaceId);
        // The enum columns round-trip by name.
        Assert.Equal(VisibilityResourceType.Scene, loaded.ResourceType);
        Assert.Equal(resourceId, loaded.ResourceId);
        Assert.Equal(VisibilityState.Hidden, loaded.Visibility);
    }

    [Fact]
    public async Task Update_persists_a_visibility_change()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var seeded = await SeedRuleAsync(org, ws, visibility: VisibilityState.Hidden);

        await using (var context = CreateContext())
        {
            var repository = new VisibilityRuleRepository(context);
            var loaded = await repository.FindByIdAsync(org, ws, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.ChangeVisibility(VisibilityState.Visible, _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new VisibilityRuleRepository(context);
            var reloaded = await repository.FindByIdAsync(org, ws, seeded.Id, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(VisibilityState.Visible, reloaded.Visibility);
            Assert.True(reloaded.IsVisibleToAudience());
        }
    }

    // --- Tenant + workspace isolation (negative authorization) -----------------

    [Fact]
    public async Task FindById_does_not_return_a_rule_through_a_foreign_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var rule = await SeedRuleAsync(organization.Id, workspaceA.Id);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        Assert.NotNull(await repository.FindByIdAsync(organization.Id, workspaceA.Id, rule.Id, CancellationToken.None));
        // The same rule id under another workspace is never returned (threat T5/T1).
        Assert.Null(await repository.FindByIdAsync(organization.Id, workspaceB.Id, rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FindById_does_not_return_a_rule_through_a_foreign_tenant()
    {
        var (orgA, wsA) = await SeedWorkspaceAsync(_organizationSlugA, _workspaceSlugA);
        var (orgB, _) = await SeedWorkspaceAsync(_organizationSlugB, _workspaceSlugB);
        var rule = await SeedRuleAsync(orgA, wsA);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        Assert.NotNull(await repository.FindByIdAsync(orgA, wsA, rule.Id, CancellationToken.None));
        // The same rule id under another tenant is never returned (threat T5/T1).
        Assert.Null(await repository.FindByIdAsync(orgB, wsA, rule.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_is_tenant_and_workspace_scoped_and_deterministically_ordered()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var first = await SeedRuleAsync(organization.Id, workspaceA.Id);
        var second = await SeedRuleAsync(organization.Id, workspaceA.Id);
        var other = await SeedRuleAsync(organization.Id, workspaceB.Id);

        var expected = new[] { first, second }
            .OrderBy(rule => rule.Id)
            .Select(rule => rule.Id)
            .ToArray();

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var listed = await repository.ListByWorkspaceAsync(organization.Id, workspaceA.Id, CancellationToken.None);

        Assert.Equal(expected, listed.Select(rule => rule.Id).ToArray());
        Assert.DoesNotContain(listed, rule => rule.Id == other.Id);
    }

    [Fact]
    public async Task ListByResource_returns_only_rules_for_that_resource_type_and_id()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var resourceId = Guid.NewGuid();
        // Two rules for the same resource (the non-unique index permits this).
        var ruleA = await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden);
        var ruleB = await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible);
        // Same id but a different resource TYPE -> excluded.
        await SeedRuleAsync(org, ws, VisibilityResourceType.Scene, resourceId);
        // Same type but a different id -> excluded.
        await SeedRuleAsync(org, ws, VisibilityResourceType.Entity, Guid.NewGuid());

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var listed = await repository.ListByResourceAsync(
            org, ws, await SessionIdAsync(org, ws), VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, rule => rule.Id == ruleA.Id);
        Assert.Contains(listed, rule => rule.Id == ruleB.Id);
    }

    [Fact]
    public async Task ListByResource_does_not_borrow_another_workspaces_rules()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspaceA = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspaceB = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var resourceId = Guid.NewGuid();
        await SeedRuleAsync(organization.Id, workspaceA.Id, VisibilityResourceType.ContentBlock, resourceId);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        // The same resource id queried in workspace B returns nothing.
        var listed = await repository.ListByResourceAsync(
            organization.Id,
            workspaceB.Id,
            await SessionIdAsync(organization.Id, workspaceB.Id),
            VisibilityResourceType.ContentBlock,
            resourceId,
            CancellationToken.None);
        Assert.Empty(listed);
    }

    // --- Session isolation (CORE-SVIS-001, the cross-session leak) --------------

    [Fact]
    public async Task ListByResource_is_session_scoped_and_never_borrows_another_sessions_rules()
    {
        // THE CROWN-JEWEL at the repository level: a workspace runs two concurrent sessions A and B. A
        // rule revealed for a resource in session A is returned for session A but NEVER for session B,
        // so a reveal in one session can never be read through a concurrent session of the same
        // workspace (threat T5/T3).
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var resourceId = Guid.NewGuid();
        var ruleInA = await SeedRuleAsync(
            org, ws, VisibilityResourceType.Scene, resourceId, VisibilityState.Visible, sessionA.Id);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        var inA = await repository.ListByResourceAsync(
            org, ws, sessionA.Id, VisibilityResourceType.Scene, resourceId, CancellationToken.None);
        Assert.Single(inA);
        Assert.Equal(ruleInA.Id, inA[0].Id);

        // Session B sees nothing for the same resource — the reveal is bounded to session A.
        var inB = await repository.ListByResourceAsync(
            org, ws, sessionB.Id, VisibilityResourceType.Scene, resourceId, CancellationToken.None);
        Assert.Empty(inB);
    }

    [Fact]
    public async Task ListByResourceAcrossSessions_returns_rules_from_every_session_for_the_role_level_path()
    {
        // The workspace-wide, session-agnostic lookup (used by the role-level asset-download and
        // entity-search reads) returns the resource's rules across ALL sessions of the workspace.
        var (org, ws) = await SeedWorkspaceAsync();
        var sessionA = await SeedSessionAsync(org, ws, "Session A");
        var sessionB = await SeedSessionAsync(org, ws, "Session B");
        var resourceId = Guid.NewGuid();
        var ruleInA = await SeedRuleAsync(
            org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Visible, sessionA.Id);
        var ruleInB = await SeedRuleAsync(
            org, ws, VisibilityResourceType.Entity, resourceId, VisibilityState.Hidden, sessionB.Id);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        var across = await repository.ListByResourceAcrossSessionsAsync(
            org, ws, VisibilityResourceType.Entity, resourceId, CancellationToken.None);

        Assert.Equal(2, across.Count);
        Assert.Contains(across, rule => rule.Id == ruleInA.Id);
        Assert.Contains(across, rule => rule.Id == ruleInB.Id);
    }

    // --- Foreign keys ----------------------------------------------------------

    [Fact]
    public async Task Foreign_key_rejects_a_rule_for_a_nonexistent_organization()
    {
        var (org, ws) = await SeedWorkspaceAsync();
        var session = await SeedSessionAsync(org, ws);
        // A real workspace and session but a tenant id that does not exist.
        var rule = VisibilityRule.Create(
            Guid.NewGuid(), ws, session.Id, VisibilityResourceType.Entity, Guid.NewGuid(), VisibilityState.Hidden, _createdAt);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(rule, CancellationToken.None));
    }

    [Fact]
    public async Task Foreign_key_rejects_a_rule_for_a_nonexistent_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var rule = VisibilityRule.Create(
            organization.Id, Guid.NewGuid(), Guid.NewGuid(), VisibilityResourceType.Entity, Guid.NewGuid(), VisibilityState.Hidden, _createdAt);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(rule, CancellationToken.None));
    }

    [Fact]
    public async Task Foreign_key_rejects_a_rule_for_a_nonexistent_session()
    {
        // A real tenant and workspace but a session id that does not exist (CORE-SVIS-001 added the
        // session foreign key): the rule is rejected at the database boundary.
        var (org, ws) = await SeedWorkspaceAsync();
        var rule = VisibilityRule.Create(
            org, ws, Guid.NewGuid(), VisibilityResourceType.Entity, Guid.NewGuid(), VisibilityState.Hidden, _createdAt);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(rule, CancellationToken.None));
    }

    [Fact]
    public async Task Resource_id_is_not_a_foreign_key_so_any_resource_id_persists()
    {
        // Documents the polymorphic reference: resource_id has NO foreign key (it spans
        // scenes/content_blocks/entities), so a rule with an arbitrary resource id persists. Ensuring
        // the resource exists and is in the same workspace is the create-rule flow's responsibility.
        var (org, ws) = await SeedWorkspaceAsync();
        var arbitraryResourceId = Guid.NewGuid();

        var rule = await SeedRuleAsync(org, ws, VisibilityResourceType.ContentBlock, arbitraryResourceId);

        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);
        var loaded = await repository.FindByIdAsync(org, ws, rule.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(arbitraryResourceId, loaded.ResourceId);
    }

    // --- Guards ----------------------------------------------------------------

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByResource_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new VisibilityRuleRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByResourceAsync(
            Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), VisibilityResourceType.Entity, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByResourceAsync(
            Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), VisibilityResourceType.Entity, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByResourceAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, VisibilityResourceType.Entity, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByResourceAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VisibilityResourceType.Entity, Guid.Empty, CancellationToken.None));
    }
}
