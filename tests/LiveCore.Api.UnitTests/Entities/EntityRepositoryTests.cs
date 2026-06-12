using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="EntityRepository"/> (CORE-ENT-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the foreign keys
/// into <c>organizations</c>/<c>workspaces</c>/<c>entity_types</c> and the attribute-values
/// round-trip are exercised on every test run without any database server or Docker. The behaviors
/// under test (scoped equality lookups, the deterministic ordering, full isolation between
/// workspaces and tenants, list-by-type scoping and the rename/redefine round-trip) are relational
/// semantics shared with PostgreSQL; provider-specific verification happens against PostgreSQL in
/// the deployment pipeline (livecore-deploy) and the isolation test story.
///
/// At the aggregate + repository level (no HTTP endpoints — csv/api_routes.csv defines no entity
/// route; entity relationships, template loading and search are CORE-ENT-003..005):
/// <list type="bullet">
///   <item>ROUND-TRIP: an entity (including its attribute-values JSON and its entity_type_id)
///   round-trips through the database, and a rename + redefine-values is reflected on the next
///   read.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in docs/07_SECURITY_THREAT_MODEL.md;
///   threat T1 broken object-level authorization; docs/06_AUTHORIZATION_MATRIX.md: organization
///   boundary checked before workspace boundary): an entity in workspace W1 is never returned via
///   workspace W2; an entity in organization A is never resolved via organization B (both
///   directions); ListByWorkspace and ListByType never borrow another workspace's or tenant's
///   entities; and the foreign keys reject an entity referencing a non-existent organization,
///   workspace or entity type.</item>
///   <item>SAME-WORKSPACE-TYPE BOUNDARY: the simple entity_type_id foreign key guarantees the type
///   EXISTS but NOT that it is in the same workspace; that coupling is the application layer's
///   responsibility (mirrors ContentBlock/scene_id). One test documents this precisely.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
///
/// THE TEMPLATE BOUNDARY (docs/04): all example names/values are GENERIC and NEUTRAL ("alpha",
/// "beta", attribute JSON like {"label":"value"}) — no vertical vocabulary appears, proving the
/// entity is stored as generic data (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntityRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private const string _validValues = """{"label":"value"}""";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntityRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database alive while every step
        // still uses its own context, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on so the FK
        // constraints in the model are genuinely exercised.
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

    private async Task<EntityType> SeedEntityTypeAsync(Guid organizationId, Guid workspaceId, string typeKey)
    {
        var entityType = EntityType.Create(organizationId, workspaceId, typeKey, "Display", "{}", _createdAt);
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        Assert.Equal(EntityTypeAddResult.Added, await repository.AddAsync(entityType, CancellationToken.None));
        return entityType;
    }

    private async Task<Entity> SeedEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name = "alpha",
        string values = _validValues)
    {
        var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, values, _createdAt);
        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.Equal(EntityAddResult.Added, await repository.AddAsync(entity, CancellationToken.None));
        return entity;
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task Entity_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var seeded = await SeedEntityAsync(
            organization.Id, workspace.Id, entityType.Id, "sample entity", _validValues);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal(entityType.Id, loaded.EntityTypeId);
        Assert.Equal("sample entity", loaded.Name);
        // The attribute-values JSON survives the round-trip verbatim.
        Assert.Equal(_validValues, loaded.AttributeValues);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Update_persists_a_rename_and_a_values_redefine()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var seeded = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "Original");

        const string newValues = """{"count":7}""";

        await using (var context = CreateContext())
        {
            var repository = new EntityRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Rename("Renamed", _updatedAt);
            loaded.RedefineAttributeValues(newValues, _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new EntityRepository(context);
            var reloaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal("Renamed", reloaded.Name);
            Assert.Equal(newValues, reloaded.AttributeValues);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
            // The tenant boundary, the workspace, the id and the type never moved.
            Assert.Equal(organization.Id, reloaded.OrganizationId);
            Assert.Equal(workspace.Id, reloaded.WorkspaceId);
            Assert.Equal(seeded.Id, reloaded.Id);
            Assert.Equal(entityType.Id, reloaded.EntityTypeId);
        }
    }

    // --- Listing (deterministic order) -----------------------------------------

    [Fact]
    public async Task ListByWorkspace_returns_entities_in_deterministic_id_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var first = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "first");
        var second = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "second");
        var third = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "third");

        // UUIDv7 ids are time-ordered; sort independently so the assertion is not coupled to UUIDv7
        // monotonicity.
        var expected = new[] { first, second, third }
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToArray();

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var firstCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        var secondCall = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(expected, firstCall.Select(entity => entity.Id).ToArray());
        // Repeating the query yields the SAME order: the sequence is deterministic.
        Assert.Equal(
            firstCall.Select(entity => entity.Id).ToArray(),
            secondCall.Select(entity => entity.Id).ToArray());
    }

    [Fact]
    public async Task ListByWorkspace_for_a_workspace_with_no_entities_is_empty()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        var entities = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(entities);
    }

    [Fact]
    public async Task ListByType_returns_only_the_named_types_entities()
    {
        // Two types in the same workspace; ListByType returns only the instances of the named type.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var typeAlpha = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var typeBeta = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");
        var alpha1 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-1");
        var alpha2 = await SeedEntityAsync(organization.Id, workspace.Id, typeAlpha.Id, "alpha-2");
        var beta1 = await SeedEntityAsync(organization.Id, workspace.Id, typeBeta.Id, "beta-1");

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var alphaEntities = await repository.ListByTypeAsync(
            organization.Id, workspace.Id, typeAlpha.Id, CancellationToken.None);

        Assert.Equal(2, alphaEntities.Count);
        Assert.Contains(alphaEntities, entity => entity.Id == alpha1.Id);
        Assert.Contains(alphaEntities, entity => entity.Id == alpha2.Id);
        Assert.DoesNotContain(alphaEntities, entity => entity.Id == beta1.Id);
        // Deterministic id order.
        var expected = new[] { alpha1, alpha2 }.OrderBy(e => e.Id).Select(e => e.Id).ToArray();
        Assert.Equal(expected, alphaEntities.Select(e => e.Id).ToArray());
    }

    [Fact]
    public async Task ListByType_for_a_type_with_no_entities_is_empty()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        var entities = await repository.ListByTypeAsync(
            organization.Id, workspace.Id, entityType.Id, CancellationToken.None);

        Assert.Empty(entities);
    }

    // --- Surrogate identity ----------------------------------------------------

    [Fact]
    public async Task Many_entities_with_the_same_name_and_type_can_coexist_in_one_workspace()
    {
        // An entity has no natural key: it is identified only by its surrogate id, so a workspace
        // may hold many entities with the same name and type, each resolving independently by id.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var first = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "same name");
        var second = await SeedEntityAsync(organization.Id, workspace.Id, entityType.Id, "same name");

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(
            organization.Id, workspace.Id, second.Id, CancellationToken.None));
        var entities = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);
        Assert.Equal(2, entities.Count);
    }

    // --- FindById negatives -----------------------------------------------------

    [Fact]
    public async Task FindById_for_a_missing_entity_returns_null()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    // --- Empty-id guards -------------------------------------------------------

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRepository(context);

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
        var repository = new EntityRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ListByType_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByTypeAsync(
                Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByTypeAsync(
                Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByTypeAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // --- Workspace / tenant isolation negatives (both directions) --------------

    [Fact]
    public async Task An_entity_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): an entity exists in workspace W1 only.
        // Looking it up by id under workspace W2 must return null even though both the entity and W2
        // exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeInW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha");
        var inW1 = await SeedEntityAsync(organization.Id, workspace1.Id, typeInW1.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task An_entity_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md:
        // organization boundary checked before workspace boundary): an entity exists in a workspace
        // owned by organization A. Looking the SAME workspace id and entity id up under organization
        // B's id must return null even though the workspace id and entity id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var typeInA = await SeedEntityTypeAsync(organizationA.Id, workspaceInA.Id, "type-alpha");
        var inA = await SeedEntityAsync(organizationA.Id, workspaceInA.Id, typeInA.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_workspaces_entities()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var typeInW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha");
        var typeInW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-beta");
        var inW1 = await SeedEntityAsync(organization.Id, workspace1.Id, typeInW1.Id);
        var inW2 = await SeedEntityAsync(organization.Id, workspace2.Id, typeInW2.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var w1Entities = await repository.ListByWorkspaceAsync(
            organization.Id, workspace1.Id, CancellationToken.None);

        Assert.Equal(inW1.Id, Assert.Single(w1Entities).Id);
        Assert.DoesNotContain(w1Entities, entity => entity.Id == inW2.Id);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_tenants_entities()
    {
        // The list is tenant-scoped: querying organization B's id for a workspace owned by
        // organization A returns nothing (threat T5; the organization boundary is checked before the
        // workspace boundary).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var typeInA = await SeedEntityTypeAsync(organizationA.Id, workspaceInA.Id, "type-alpha");
        await SeedEntityAsync(organizationA.Id, workspaceInA.Id, typeInA.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        var underA = await repository.ListByWorkspaceAsync(
            organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var underB = await repository.ListByWorkspaceAsync(
            organizationB.Id, workspaceInA.Id, CancellationToken.None);

        Assert.Single(underA);
        Assert.Empty(underB);
    }

    [Fact]
    public async Task ListByType_never_crosses_the_workspace_or_tenant_boundary()
    {
        // ListByType is also tenant- and workspace-scoped: the same type id queried under a foreign
        // workspace or tenant returns nothing (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspace1 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        var typeInW1 = await SeedEntityTypeAsync(organizationA.Id, workspace1.Id, "type-alpha");
        await SeedEntityAsync(organizationA.Id, workspace1.Id, typeInW1.Id);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        Assert.Single(await repository.ListByTypeAsync(
            organizationA.Id, workspace1.Id, typeInW1.Id, CancellationToken.None));
        // Wrong workspace.
        Assert.Empty(await repository.ListByTypeAsync(
            organizationA.Id, workspace2.Id, typeInW1.Id, CancellationToken.None));
        // Wrong tenant.
        Assert.Empty(await repository.ListByTypeAsync(
            organizationB.Id, workspace1.Id, typeInW1.Id, CancellationToken.None));
    }

    // --- Foreign-key enforcement -----------------------------------------------

    [Fact]
    public async Task An_entity_cannot_reference_an_entity_type_that_does_not_exist()
    {
        // The entity_type_id foreign key is enforced (PRAGMA foreign_keys = ON): an entity for a
        // non-existent type is rejected, so a dangling entity can never exist outside a real type
        // (threat T5). The organization and workspace are real to isolate the type FK as the failing
        // constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = Entity.Create(
            organization.Id, workspace.Id, Guid.CreateVersion7(), "alpha", _validValues, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task An_entity_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced: an entity whose workspace does not exist is
        // rejected (threat T5). The organization is real to isolate the workspace FK; a non-existent
        // type id is supplied because no type can exist in the missing workspace.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = Entity.Create(
            organization.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), "alpha", _validValues, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task An_entity_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: an entity whose tenant does not exist is
        // rejected even when the workspace and type exist, so the row can never carry a tenant
        // boundary that is not a real organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var entityType = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        var ghost = Entity.Create(
            Guid.CreateVersion7(), workspace.Id, entityType.Id, "alpha", _validValues, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    // --- Same-workspace-type boundary (mirrors ContentBlock/scene_id) ----------

    [Fact]
    public async Task A_simple_type_fk_allows_a_type_from_another_workspace_the_application_layer_must_couple()
    {
        // SAME-WORKSPACE-TYPE BOUNDARY (carry-over, deferred to the create-entity endpoint story):
        // the entity_type_id foreign key is a SIMPLE single-column FK into entity_types(id) — like
        // ContentBlock.scene_id. It guarantees the referenced type EXISTS but does NOT guarantee the
        // type is in the entity's OWN workspace. This test documents exactly that: an entity stored
        // in workspace W1 may reference a type that lives in workspace W2, and the insert is
        // referentially VALID (the FK is satisfied because the type row exists). Preventing a
        // cross-workspace type reference is the responsibility of the future create-entity
        // application flow, which resolves the type via the workspace-scoped
        // IEntityTypeRepository.FindByIdAsync(org, ws, typeId) before creating the entity. The DB FK
        // alone does NOT enforce the same-workspace coupling.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        // The type lives in workspace W2.
        var typeInW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-alpha");

        // The entity is stored in workspace W1 but references W2's type. The simple FK is satisfied
        // (the type row exists), so the insert succeeds — the DB does not couple the workspaces.
        var entity = Entity.Create(
            organization.Id, workspace1.Id, typeInW2.Id, "alpha", _validValues, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityRepository(context);
        Assert.Equal(EntityAddResult.Added, await repository.AddAsync(entity, CancellationToken.None));

        // The entity is genuinely persisted in W1, referencing a type whose own workspace is W2 —
        // proving the FK guarantees existence, not same-workspace membership. The same-workspace
        // coupling is the application layer's responsibility (mirrors ContentBlock/scene_id).
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, entity.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(typeInW2.Id, loaded.EntityTypeId);
        Assert.Equal(workspace1.Id, loaded.WorkspaceId);
        Assert.NotEqual(workspace1.Id, workspace2.Id);
    }
}
