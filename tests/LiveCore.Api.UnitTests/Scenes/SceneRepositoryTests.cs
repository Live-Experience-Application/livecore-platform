// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Scenes;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="SceneRepository"/>
/// (CORE-SCENE-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the
/// foreign keys into <c>organizations</c>/<c>workspaces</c> and the deterministic
/// ordered retrieval are exercised on every test run without any database server or
/// Docker. The behaviors under test (scoped equality lookups, the deterministic
/// (scene_order, id) ordering, full isolation between workspaces and between tenants,
/// and the rename/reorder round-trip) are relational semantics shared with
/// PostgreSQL; provider-specific verification happens against PostgreSQL in the
/// deployment pipeline (livecore-deploy) and the isolation test story.
///
/// At the aggregate + repository level (no HTTP endpoints yet — the create/reorder
/// endpoints, content blocks, host/participant DTO projection and content validation
/// are CORE-SCENE-002..005):
/// <list type="bullet">
///   <item>ORDERING: ListByWorkspaceAsync returns a workspace's scenes in
///   deterministic (scene_order, id) order, even with duplicate orders, and a reorder
///   is reflected on the next list (the ordering feature made observable).</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization;
///   docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before workspace
///   boundary): a scene in workspace W1 is never returned via workspace W2; a scene in
///   organization A is never resolved via organization B; ListByWorkspaceAsync never
///   borrows another workspace's or tenant's scenes; and the foreign keys reject a
///   scene referencing a non-existent workspace or organization.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class SceneRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SceneRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive
        // while every step still uses its own context, so reads genuinely round-trip
        // through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so
        // the FK constraints in the model are genuinely exercised.
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

    private async Task<Scene> SeedSceneAsync(Guid organizationId, Guid workspaceId, string title, int order)
    {
        var scene = Scene.Create(organizationId, workspaceId, title, order, _createdAt);
        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        Assert.Equal(SceneAddResult.Added, await repository.AddAsync(scene, CancellationToken.None));
        return scene;
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task Scene_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment", 2);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal("Opening Segment", loaded.Title);
        Assert.Equal(2, loaded.Order);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Update_persists_a_rename_and_a_reorder()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedSceneAsync(organization.Id, workspace.Id, "Original", 1);

        await using (var context = CreateContext())
        {
            var repository = new SceneRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Rename("Renamed", _updatedAt);
            loaded.Reorder(8, _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SceneRepository(context);
            var reloaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal("Renamed", reloaded.Title);
            Assert.Equal(8, reloaded.Order);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
            // The tenant boundary, the workspace and the id never moved.
            Assert.Equal(organization.Id, reloaded.OrganizationId);
            Assert.Equal(workspace.Id, reloaded.WorkspaceId);
            Assert.Equal(seeded.Id, reloaded.Id);
        }
    }

    // --- Ordering (the headline feature) ---------------------------------------

    [Fact]
    public async Task ListByWorkspace_returns_scenes_in_deterministic_order_position_order()
    {
        // Seed scenes with mixed, out-of-insertion-order positions and assert the
        // returned sequence is sorted ascending by scene_order (the ordering feature).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var third = await SeedSceneAsync(organization.Id, workspace.Id, "Third", 20);
        var first = await SeedSceneAsync(organization.Id, workspace.Id, "First", 0);
        var second = await SeedSceneAsync(organization.Id, workspace.Id, "Second", 10);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        var scenes = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, scenes.Select(scene => scene.Id).ToArray());
        Assert.Equal(new[] { 0, 10, 20 }, scenes.Select(scene => scene.Order).ToArray());
    }

    [Fact]
    public async Task ListByWorkspace_breaks_duplicate_orders_deterministically_by_id()
    {
        // Duplicate orders are allowed (the order column is non-unique). The repository
        // makes the result deterministic by ordering on (scene_order, id), so equal
        // orders are sorted by their surrogate id — a stable, repeatable sequence.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        // All three share scene_order 5; UUIDv7 ids are time-ordered, so the expected
        // tie-break order is creation order.
        var sceneA = await SeedSceneAsync(organization.Id, workspace.Id, "A", 5);
        var sceneB = await SeedSceneAsync(organization.Id, workspace.Id, "B", 5);
        var sceneC = await SeedSceneAsync(organization.Id, workspace.Id, "C", 5);

        // Independently sort the seeded ids the same way the repository must
        // (ascending by id) so the assertion is not coupled to UUIDv7 monotonicity.
        var expected = new[] { sceneA, sceneB, sceneC }
            .OrderBy(scene => scene.Id)
            .Select(scene => scene.Id)
            .ToArray();

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var firstCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        var secondCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(expected, firstCall.Select(scene => scene.Id).ToArray());
        // Repeating the query yields the SAME order: the sequence is deterministic.
        Assert.Equal(
            firstCall.Select(scene => scene.Id).ToArray(),
            secondCall.Select(scene => scene.Id).ToArray());
    }

    [Fact]
    public async Task ListByWorkspace_reflects_a_reorder_on_the_next_list()
    {
        // After a reorder the list reflects the new position order.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var alpha = await SeedSceneAsync(organization.Id, workspace.Id, "Alpha", 0);
        var beta = await SeedSceneAsync(organization.Id, workspace.Id, "Beta", 1);

        await using (var context = CreateContext())
        {
            var repository = new SceneRepository(context);
            var before = await repository.ListByWorkspaceAsync(
                organization.Id, workspace.Id, CancellationToken.None);
            Assert.Equal(new[] { alpha.Id, beta.Id }, before.Select(scene => scene.Id).ToArray());

            // Move Alpha behind Beta.
            var loadedAlpha = await repository.FindByIdAsync(
                organization.Id, workspace.Id, alpha.Id, CancellationToken.None);
            Assert.NotNull(loadedAlpha);
            loadedAlpha.Reorder(5, _updatedAt);
            await repository.UpdateAsync(loadedAlpha, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new SceneRepository(context);
            var after = await repository.ListByWorkspaceAsync(
                organization.Id, workspace.Id, CancellationToken.None);

            // Beta (order 1) now precedes Alpha (order 5).
            Assert.Equal(new[] { beta.Id, alpha.Id }, after.Select(scene => scene.Id).ToArray());
        }
    }

    [Fact]
    public async Task ListByWorkspace_for_a_workspace_with_no_scenes_is_empty()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        var scenes = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(scenes);
    }

    // --- FindById negatives ----------------------------------------------------

    [Fact]
    public async Task FindById_for_a_missing_scene_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // --- Tenant / workspace isolation negatives --------------------------------

    [Fact]
    public async Task A_scene_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a scene exists in workspace W1
        // only. Looking it up by id under workspace W2 must return null even though
        // both the scene and W2 exist. A cross-workspace lookup returns null/deny.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedSceneAsync(organization.Id, workspace1.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_scene_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary): a scene exists in a workspace owned by organization A.
        // Looking the SAME workspace id and scene id up under organization B's id must
        // return null/deny even though the workspace id and scene id are correct: the
        // wrong tenant denies access.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var inA = await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    // --- FindByIdInOrganization (org-scoped, workspace-agnostic; CORE-SCENE-003) ----

    [Fact]
    public async Task FindByIdInOrganization_finds_a_scene_in_its_organization_regardless_of_workspace()
    {
        // The org-only lookup (used by the by-scene-id content-block route, which does not
        // know the workspace up front) resolves a scene by id WITHIN the tenant and exposes
        // the scene's own workspace id off the returned row, so the caller can then
        // authorize against that workspace's membership (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var found = await repository.FindByIdInOrganizationAsync(
            organization.Id, scene.Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(scene.Id, found.Id);
        Assert.Equal(workspace.Id, found.WorkspaceId);
    }

    [Fact]
    public async Task FindByIdInOrganization_for_a_scene_in_another_tenant_returns_null()
    {
        // Mandatory negative foreign-tenant test for the NEW org-only lookup (threat T5):
        // a scene exists in org A; looking the SAME scene id up under organization B's id
        // must return null even though the scene id is correct. The predicate leads with
        // the organization column, so the surrogate id never crosses the tenant boundary.
        // This regression-locks the organization predicate of FindByIdInOrganizationAsync
        // (dropping it would let a foreign-tenant scene be reached by id).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var inA = await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var underA = await repository.FindByIdInOrganizationAsync(
            organizationA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdInOrganizationAsync(
            organizationB.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task FindByIdInOrganization_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdInOrganizationAsync(
                Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdInOrganizationAsync(
                Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_workspaces_scenes()
    {
        // The ordered list is workspace-scoped: it returns only the named workspace's
        // scenes and never borrows a sibling workspace's scenes (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedSceneAsync(organization.Id, workspace1.Id, "W1 Segment", 0);
        var inW2 = await SeedSceneAsync(organization.Id, workspace2.Id, "W2 Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var w1Scenes = await repository.ListByWorkspaceAsync(
            organization.Id, workspace1.Id, CancellationToken.None);

        Assert.Equal(inW1.Id, Assert.Single(w1Scenes).Id);
        Assert.DoesNotContain(w1Scenes, scene => scene.Id == inW2.Id);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_tenants_scenes()
    {
        // The ordered list is tenant-scoped: querying organization B's id for a
        // workspace owned by organization A returns nothing — a foreign tenant's
        // scenes are never returned even when the workspace id is correct (threat T5;
        // the organization boundary is checked before the workspace boundary).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var underA = await repository.ListByWorkspaceAsync(
            organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var underB = await repository.ListByWorkspaceAsync(
            organizationB.Id, workspaceInA.Id, CancellationToken.None);

        Assert.Single(underA);
        Assert.Empty(underB);
    }

    // --- Surrogate identity ----------------------------------------------------

    [Fact]
    public async Task Many_scenes_with_the_same_title_and_order_can_coexist_in_one_workspace()
    {
        // A scene has no natural key and a non-unique order column: it is identified
        // only by its surrogate id, so a workspace may hold many scenes with the same
        // title AND the same order, each resolving independently by id.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var first = await SeedSceneAsync(organization.Id, workspace.Id, "Same Title", 3);
        var second = await SeedSceneAsync(organization.Id, workspace.Id, "Same Title", 3);

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, second.Id, CancellationToken.None));
        // Both appear in the workspace's scene list (the non-unique order is allowed).
        var scenes = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        Assert.Equal(2, scenes.Count);
    }

    // --- Foreign-key enforcement -----------------------------------------------

    [Fact]
    public async Task A_scene_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a scene
        // for a non-existent workspace is rejected, so a dangling scene can never exist
        // outside a real workspace boundary (threat T5). The organization is real to
        // isolate the workspace FK as the failing constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = Scene.Create(organization.Id, Guid.CreateVersion7(), "Segment", 0, _createdAt);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_scene_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a scene whose tenant does not
        // exist is rejected even when the workspace exists, so the row can never carry
        // a tenant boundary that is not a real organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Scene.Create(Guid.CreateVersion7(), workspace.Id, "Segment", 0, _createdAt);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    // --- Bounded pagination + max-order (CORE-DX-003) ---------------------------

    [Fact]
    public async Task ListPageByWorkspace_returns_a_bounded_page_in_the_deterministic_order()
    {
        // The paged read returns at most `take` scenes starting at `skip`, in the SAME (scene_order, id) order
        // as the unbounded list — so a caller can page deterministically without ever materializing the whole
        // workspace (threat T9).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var first = await SeedSceneAsync(organization.Id, workspace.Id, "First", 0);
        var second = await SeedSceneAsync(organization.Id, workspace.Id, "Second", 1);
        var third = await SeedSceneAsync(organization.Id, workspace.Id, "Third", 2);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var firstPage = await repository.ListPageByWorkspaceAsync(
            organization.Id, workspace.Id, skip: 0, take: 2, CancellationToken.None);
        var secondPage = await repository.ListPageByWorkspaceAsync(
            organization.Id, workspace.Id, skip: 2, take: 2, CancellationToken.None);

        Assert.Equal(new[] { first.Id, second.Id }, firstPage.Select(scene => scene.Id).ToArray());
        Assert.Equal(new[] { third.Id }, secondPage.Select(scene => scene.Id).ToArray());
    }

    [Fact]
    public async Task ListPageByWorkspace_never_returns_another_tenants_scenes()
    {
        // The paged read is tenant-scoped exactly like the unbounded list: organization B's id never returns
        // organization A's scenes even when the workspace id is correct (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Segment", 0);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var underB = await repository.ListPageByWorkspaceAsync(
            organizationB.Id, workspaceInA.Id, skip: 0, take: 50, CancellationToken.None);

        Assert.Empty(underB);
    }

    [Fact]
    public async Task ListPageByWorkspace_rejects_a_negative_skip_or_non_positive_take()
    {
        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListPageByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), skip: -1, take: 10, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.ListPageByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), skip: 0, take: 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetMaxOrderByWorkspace_returns_null_for_an_empty_workspace()
    {
        // The append-to-end create relies on this null to assign the first order (0) without loading any scenes.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var max = await repository.GetMaxOrderByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Null(max);
    }

    [Fact]
    public async Task GetMaxOrderByWorkspace_returns_the_largest_order_scoped_to_the_workspace()
    {
        // The maximum is computed server-side and is tenant- AND workspace-scoped: a sibling workspace's higher
        // order never influences the result, so the create appends to the end of the RIGHT workspace.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var sibling = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        await SeedSceneAsync(organization.Id, workspace.Id, "A", 0);
        await SeedSceneAsync(organization.Id, workspace.Id, "B", 7);
        await SeedSceneAsync(organization.Id, sibling.Id, "Sibling", 99);

        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        var max = await repository.GetMaxOrderByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(7, max);
    }

    [Fact]
    public async Task GetMaxOrderByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new SceneRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetMaxOrderByWorkspaceAsync(
                Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.GetMaxOrderByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }
}
