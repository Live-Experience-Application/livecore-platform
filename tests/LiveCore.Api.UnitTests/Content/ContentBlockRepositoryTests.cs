using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Content;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="ContentBlockRepository"/>
/// (CORE-SCENE-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the
/// foreign keys into <c>organizations</c>/<c>workspaces</c>/<c>scenes</c>, the
/// content-type-stored-as-name conversion and the revision round-trip are exercised on
/// every test run without any database server or Docker. The behaviors under test
/// (scoped equality lookups, the deterministic ordering, full isolation between scenes,
/// workspaces and tenants, and the revision update round-trip) are relational semantics
/// shared with PostgreSQL; provider-specific verification happens against PostgreSQL in
/// the deployment pipeline (livecore-deploy) and the isolation test story.
///
/// At the aggregate + repository level (no HTTP endpoints yet — the scene content
/// endpoints, host/participant DTO projection and content validation are
/// CORE-SCENE-003..005, and the server-side visibility decision is the Visibility
/// epic):
/// <list type="bullet">
///   <item>REVISIONS: a revision (a replaced body and an incremented
///   revision_number) round-trips through the database (the headline feature made
///   observable).</item>
///   <item>CONTENT TYPE: the content type is stored and loaded by its stable NAME, not
///   its numeric value.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in
///   docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization;
///   docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before workspace
///   boundary): a block in scene S1 is never returned via scene S2; a block in
///   workspace W1 is never returned via workspace W2; a block in organization A is
///   never resolved via organization B; ListByScene never borrows another scene's,
///   workspace's or tenant's blocks; and the foreign keys reject a block referencing a
///   non-existent scene, workspace or organization.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
/// </summary>
public sealed class ContentBlockRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ContentBlockRepositoryTests()
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

    private async Task<Scene> SeedSceneAsync(Guid organizationId, Guid workspaceId, string title)
    {
        var scene = Scene.Create(organizationId, workspaceId, title, 0, _createdAt);
        await using var context = CreateContext();
        var repository = new SceneRepository(context);
        Assert.Equal(SceneAddResult.Added, await repository.AddAsync(scene, CancellationToken.None));
        return scene;
    }

    private async Task<ContentBlock> SeedContentBlockAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        ContentBlockType type,
        string body)
    {
        var block = ContentBlock.Create(organizationId, workspaceId, sceneId, type, body, _createdAt);
        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        Assert.Equal(ContentBlockAddResult.Added, await repository.AddAsync(block, CancellationToken.None));
        return block;
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task Content_block_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        var seeded = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Opening narration");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(scene.Id, loaded.SceneId);
        Assert.Equal(ContentBlockType.Text, loaded.Type);
        Assert.Equal("Opening narration", loaded.Body);
        Assert.Equal(1, loaded.RevisionNumber);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task FindById_with_the_scene_scoped_overload_round_trips()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        var seeded = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Data, "{\"key\":\"value\"}");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, scene.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(scene.Id, loaded.SceneId);
    }

    // --- Revisions (the headline feature) --------------------------------------

    [Fact]
    public async Task Update_persists_a_revision_with_an_incremented_revision_number()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        var seeded = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Original");

        await using (var context = CreateContext())
        {
            var repository = new ContentBlockRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Revise(ContentBlockType.Media, "asset://ref-5", _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new ContentBlockRepository(context);
            var reloaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal("asset://ref-5", reloaded.Body);
            Assert.Equal(ContentBlockType.Media, reloaded.Type);
            // Revision 1 -> 2 persisted.
            Assert.Equal(2, reloaded.RevisionNumber);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
            // The tenant boundary, the workspace, the scene and the id never moved.
            Assert.Equal(organization.Id, reloaded.OrganizationId);
            Assert.Equal(workspace.Id, reloaded.WorkspaceId);
            Assert.Equal(scene.Id, reloaded.SceneId);
            Assert.Equal(seeded.Id, reloaded.Id);
        }
    }

    [Fact]
    public async Task Multiple_revisions_round_trip_with_a_monotonic_revision_number()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        var seeded = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "v1");

        // Apply three successive revisions, each in its own context.
        for (var revision = 2; revision <= 4; revision++)
        {
            await using var context = CreateContext();
            var repository = new ContentBlockRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Revise(ContentBlockType.Text, $"v{revision}", _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using var assertionContext = CreateContext();
        var assertionRepository = new ContentBlockRepository(assertionContext);
        var final = await assertionRepository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal("v4", final.Body);
        Assert.Equal(4, final.RevisionNumber);
    }

    [Fact]
    public async Task Content_type_is_stored_as_its_stable_name_in_the_database()
    {
        // The content_type column persists the stable NAME, not the numeric value
        // (HasConversion<string>), so the stored value stays readable and is not coupled
        // to the declaration-order integers. Read the raw column back to assert the
        // stored text.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Media, "asset://ref-1");

        await using var assertionContext = CreateContext();
        // Read the raw content_type column back as text. Only one content block exists
        // in this test, so no id filter is needed. The stored value is the enum NAME,
        // never the integer discriminator.
        var storedTypes = await assertionContext.Database
            .SqlQueryRaw<string>("SELECT content_type AS Value FROM content_blocks")
            .ToListAsync(CancellationToken.None);

        Assert.Equal(nameof(ContentBlockType.Media), Assert.Single(storedTypes));
    }

    // --- ListByScene -----------------------------------------------------------

    [Fact]
    public async Task ListByScene_returns_the_scenes_blocks_in_deterministic_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");
        var first = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "First");
        var second = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Second");
        var third = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Third");

        // UUIDv7 ids are time-ordered, so the expected order by id is creation order;
        // sort independently so the assertion is not coupled to UUIDv7 monotonicity.
        var expected = new[] { first, second, third }
            .OrderBy(block => block.Id)
            .Select(block => block.Id)
            .ToArray();

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var firstCall = await repository.ListBySceneAsync(
            organization.Id, workspace.Id, scene.Id, CancellationToken.None);
        var secondCall = await repository.ListBySceneAsync(
            organization.Id, workspace.Id, scene.Id, CancellationToken.None);

        Assert.Equal(expected, firstCall.Select(block => block.Id).ToArray());
        // Repeating the query yields the SAME order: the sequence is deterministic.
        Assert.Equal(
            firstCall.Select(block => block.Id).ToArray(),
            secondCall.Select(block => block.Id).ToArray());
    }

    [Fact]
    public async Task ListByScene_for_a_scene_with_no_blocks_is_empty()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Opening Segment");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        var blocks = await repository.ListBySceneAsync(
            organization.Id, workspace.Id, scene.Id, CancellationToken.None);

        Assert.Empty(blocks);
    }

    // --- FindById negatives -----------------------------------------------------

    [Fact]
    public async Task FindById_for_a_missing_block_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

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
    public async Task FindById_scene_scoped_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByScene_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySceneAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySceneAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListBySceneAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // --- Scene / workspace / tenant isolation negatives ------------------------

    [Fact]
    public async Task A_block_in_scene_S1_is_not_returned_via_scene_S2()
    {
        // Mandatory negative scene test (threat T5): a block exists in scene S1 only.
        // Looking it up by id under scene S2 (scene-scoped overload) must return null
        // even though both the block and S2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene1 = await SeedSceneAsync(organization.Id, workspace.Id, "Scene One");
        var scene2 = await SeedSceneAsync(organization.Id, workspace.Id, "Scene Two");
        var inS1 = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene1.Id, ContentBlockType.Text, "Body");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var foundInS1 = await repository.FindByIdAsync(
            organization.Id, workspace.Id, scene1.Id, inS1.Id, CancellationToken.None);
        var viaS2 = await repository.FindByIdAsync(
            organization.Id, workspace.Id, scene2.Id, inS1.Id, CancellationToken.None);

        Assert.NotNull(foundInS1);
        Assert.Equal(scene1.Id, foundInS1.SceneId);
        Assert.Null(viaS2);
    }

    [Fact]
    public async Task A_block_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a block exists in workspace W1
        // only. Looking it up by id under workspace W2 must return null even though both
        // the block and W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var scene1 = await SeedSceneAsync(organization.Id, workspace1.Id, "Scene One");
        var inW1 = await SeedContentBlockAsync(
            organization.Id, workspace1.Id, scene1.Id, ContentBlockType.Text, "Body");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_block_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5;
        // docs/06_AUTHORIZATION_MATRIX.md: organization boundary checked before
        // workspace boundary): a block exists in a workspace owned by organization A.
        // Looking the SAME workspace id and block id up under organization B's id must
        // return null/deny even though the workspace id and block id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var sceneInA = await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Scene One");
        var inA = await SeedContentBlockAsync(
            organizationA.Id, workspaceInA.Id, sceneInA.Id, ContentBlockType.Text, "Body");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task ListByScene_never_returns_another_scenes_blocks()
    {
        // The list is scene-scoped: it returns only the named scene's blocks and never
        // borrows a sibling scene's blocks (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene1 = await SeedSceneAsync(organization.Id, workspace.Id, "Scene One");
        var scene2 = await SeedSceneAsync(organization.Id, workspace.Id, "Scene Two");
        var inS1 = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene1.Id, ContentBlockType.Text, "S1 Body");
        var inS2 = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene2.Id, ContentBlockType.Text, "S2 Body");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var s1Blocks = await repository.ListBySceneAsync(
            organization.Id, workspace.Id, scene1.Id, CancellationToken.None);

        Assert.Equal(inS1.Id, Assert.Single(s1Blocks).Id);
        Assert.DoesNotContain(s1Blocks, block => block.Id == inS2.Id);
    }

    [Fact]
    public async Task ListByScene_never_returns_another_tenants_blocks()
    {
        // The list is tenant-scoped: querying organization B's id for a scene owned by
        // organization A returns nothing — a foreign tenant's blocks are never returned
        // even when the workspace and scene ids are correct (threat T5; the organization
        // boundary is checked before the workspace boundary).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var sceneInA = await SeedSceneAsync(organizationA.Id, workspaceInA.Id, "Scene One");
        await SeedContentBlockAsync(
            organizationA.Id, workspaceInA.Id, sceneInA.Id, ContentBlockType.Text, "Body");

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        var underA = await repository.ListBySceneAsync(
            organizationA.Id, workspaceInA.Id, sceneInA.Id, CancellationToken.None);
        var underB = await repository.ListBySceneAsync(
            organizationB.Id, workspaceInA.Id, sceneInA.Id, CancellationToken.None);

        Assert.Single(underA);
        Assert.Empty(underB);
    }

    // --- Surrogate identity ----------------------------------------------------

    [Fact]
    public async Task Many_blocks_with_the_same_body_can_coexist_in_one_scene()
    {
        // A content block has no natural key: it is identified only by its surrogate id,
        // so a scene may hold many blocks with the same body, each resolving
        // independently by id.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Scene One");
        var first = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Same Body");
        var second = await SeedContentBlockAsync(
            organization.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Same Body");

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, second.Id, CancellationToken.None));
        var blocks = await repository.ListBySceneAsync(
            organization.Id, workspace.Id, scene.Id, CancellationToken.None);
        Assert.Equal(2, blocks.Count);
    }

    // --- Foreign-key enforcement -----------------------------------------------

    [Fact]
    public async Task A_block_cannot_reference_a_scene_that_does_not_exist()
    {
        // The scene_id foreign key is enforced (PRAGMA foreign_keys = ON): a block for a
        // non-existent scene is rejected, so a dangling block can never exist outside a
        // real scene boundary (threat T5). The organization and workspace are real to
        // isolate the scene FK as the failing constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = ContentBlock.Create(
            organization.Id, workspace.Id, Guid.CreateVersion7(), ContentBlockType.Text, "Body", _createdAt);

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_block_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced: a block whose workspace does not
        // exist is rejected (threat T5). The organization is real to isolate the
        // workspace FK as the failing constraint; a non-existent scene id is supplied
        // because no scene can exist in the missing workspace.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = ContentBlock.Create(
            organization.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), ContentBlockType.Text, "Body", _createdAt);

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_block_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a block whose tenant does not
        // exist is rejected even when the workspace and scene exist, so the row can
        // never carry a tenant boundary that is not a real organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var scene = await SeedSceneAsync(organization.Id, workspace.Id, "Scene One");
        var ghost = ContentBlock.Create(
            Guid.CreateVersion7(), workspace.Id, scene.Id, ContentBlockType.Text, "Body", _createdAt);

        await using var context = CreateContext();
        var repository = new ContentBlockRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }
}
