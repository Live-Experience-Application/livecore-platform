using LiveCore.Api.Entities;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="EntityTypeRepository"/>
/// (CORE-ENT-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the foreign
/// keys into <c>organizations</c>/<c>workspaces</c>, the per-workspace unique
/// (<c>workspace_id</c>, <c>type_key</c>) index and the attribute-schema round-trip are exercised
/// on every test run without any database server or Docker. The behaviors under test (scoped
/// equality lookups, the deterministic key ordering, full isolation between workspaces and between
/// tenants, the per-workspace key uniqueness and the rename/redefine round-trip) are relational
/// semantics shared with PostgreSQL; provider-specific verification happens against PostgreSQL in
/// the deployment pipeline (livecore-deploy) and the isolation test story.
///
/// At the aggregate + repository level (no HTTP endpoints — csv/api_routes.csv defines no
/// entity-type route; entity instances, relationships, template loading and search are
/// CORE-ENT-002..005):
/// <list type="bullet">
///   <item>ROUND-TRIP: a type (including its attribute-schema JSON) round-trips through the
///   database, and a rename + redefine-schema is reflected on the next read.</item>
///   <item>PER-WORKSPACE KEY UNIQUENESS: a second type with the same key in the same workspace is
///   rejected as a duplicate and the existing row is left unchanged, while the same key in another
///   workspace coexists freely.</item>
///   <item>AUTHORIZATION / OBJECT-LEVEL ISOLATION (threat T5 in docs/07_SECURITY_THREAT_MODEL.md;
///   threat T1 broken object-level authorization; docs/06_AUTHORIZATION_MATRIX.md: organization
///   boundary checked before workspace boundary): a type in workspace W1 is never returned via
///   workspace W2; a type in organization A is never resolved via organization B (both directions);
///   ListByWorkspace never borrows another workspace's or tenant's types; and the foreign keys
///   reject a type referencing a non-existent workspace or organization.</item>
/// </list>
/// These negative cases are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md).
///
/// THE TEMPLATE BOUNDARY (docs/04): all example keys/names are GENERIC and NEUTRAL ("type-alpha",
/// "type-beta", "profile", "note") — no vertical vocabulary appears, proving the type is stored as
/// generic data (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntityTypeRepositoryTests : IDisposable
{
    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private const string _validSchema = """{"fields":[{"name":"label","kind":"text"}]}""";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntityTypeRepositoryTests()
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

    private async Task<EntityType> SeedEntityTypeAsync(
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        string displayName = "Display",
        string schema = _validSchema)
    {
        var entityType = EntityType.Create(organizationId, workspaceId, typeKey, displayName, schema, _createdAt);
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        Assert.Equal(EntityTypeAddResult.Added, await repository.AddAsync(entityType, CancellationToken.None));
        return entityType;
    }

    // --- Round-trip ------------------------------------------------------------

    [Fact]
    public async Task EntityType_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedEntityTypeAsync(
            organization.Id, workspace.Id, "type-alpha", "Type Alpha", _validSchema);

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(workspace.Id, loaded.WorkspaceId);
        Assert.Equal("type-alpha", loaded.TypeKey);
        Assert.Equal("Type Alpha", loaded.DisplayName);
        // The attribute-schema JSON survives the round-trip verbatim.
        Assert.Equal(_validSchema, loaded.AttributeSchema);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task FindByKey_resolves_a_type_by_its_canonical_key()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        // The key is canonicalized before the lookup, so a re-cased/padded key still resolves.
        var loaded = await repository.FindByKeyAsync(
            organization.Id, workspace.Id, "  TYPE-ALPHA  ", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
    }

    [Fact]
    public async Task Update_persists_a_rename_and_a_schema_redefine()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var seeded = await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha", "Original");

        const string newSchema = """{"fields":[{"name":"count","kind":"number"}]}""";

        await using (var context = CreateContext())
        {
            var repository = new EntityTypeRepository(context);
            var loaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);
            Assert.NotNull(loaded);

            loaded.Rename("Renamed", _updatedAt);
            loaded.RedefineAttributeSchema(newSchema, _updatedAt);
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new EntityTypeRepository(context);
            var reloaded = await repository.FindByIdAsync(
                organization.Id, workspace.Id, seeded.Id, CancellationToken.None);

            Assert.NotNull(reloaded);
            Assert.Equal("Renamed", reloaded.DisplayName);
            Assert.Equal(newSchema, reloaded.AttributeSchema);
            Assert.Equal(_updatedAt, reloaded.UpdatedAt);
            // The tenant boundary, the workspace, the id and the key never moved.
            Assert.Equal(organization.Id, reloaded.OrganizationId);
            Assert.Equal(workspace.Id, reloaded.WorkspaceId);
            Assert.Equal(seeded.Id, reloaded.Id);
            Assert.Equal("type-alpha", reloaded.TypeKey);
        }
    }

    // --- Listing (deterministic key order) -------------------------------------

    [Fact]
    public async Task ListByWorkspace_returns_types_in_deterministic_key_order()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        // Seed out of key order; the list must come back sorted ascending by canonical key.
        await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-gamma");
        await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-alpha");
        await SeedEntityTypeAsync(organization.Id, workspace.Id, "type-beta");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        var types = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Equal(
            new[] { "type-alpha", "type-beta", "type-gamma" },
            types.Select(entityType => entityType.TypeKey).ToArray());
    }

    [Fact]
    public async Task ListByWorkspace_for_a_workspace_with_no_types_is_empty()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);
        var types = await repository.ListByWorkspaceAsync(
            organization.Id, workspace.Id, CancellationToken.None);

        Assert.Empty(types);
    }

    // --- Per-workspace key uniqueness ------------------------------------------

    [Fact]
    public async Task A_duplicate_key_in_the_same_workspace_is_rejected_and_the_existing_row_is_unchanged()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var first = await SeedEntityTypeAsync(
            organization.Id, workspace.Id, "type-alpha", "First Name", _validSchema);

        // A second type with the SAME key in the SAME workspace is rejected as a duplicate.
        var duplicate = EntityType.Create(
            organization.Id, workspace.Id, "type-alpha", "Second Name", """{"x":1}""", _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new EntityTypeRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);
            Assert.Equal(EntityTypeAddResult.DuplicateKey, result);
        }

        // The existing row is untouched: its id, display name and schema are the first writer's.
        await using (var context = CreateContext())
        {
            var repository = new EntityTypeRepository(context);
            var loaded = await repository.FindByKeyAsync(
                organization.Id, workspace.Id, "type-alpha", CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(first.Id, loaded.Id);
            Assert.Equal("First Name", loaded.DisplayName);
            Assert.Equal(_validSchema, loaded.AttributeSchema);

            // Exactly one type with that key exists in the workspace.
            var types = await repository.ListByWorkspaceAsync(
                organization.Id, workspace.Id, CancellationToken.None);
            Assert.Single(types);
        }
    }

    [Fact]
    public async Task The_same_key_coexists_in_two_different_workspaces()
    {
        // The uniqueness is per-WORKSPACE: the same key may be defined in two different workspaces,
        // each resolving independently (threat T5: the key in W2 is a different type from W1's).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);

        var inW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha", "W1 Alpha");
        var inW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-alpha", "W2 Alpha");

        Assert.NotEqual(inW1.Id, inW2.Id);

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        var fromW1 = await repository.FindByKeyAsync(
            organization.Id, workspace1.Id, "type-alpha", CancellationToken.None);
        var fromW2 = await repository.FindByKeyAsync(
            organization.Id, workspace2.Id, "type-alpha", CancellationToken.None);

        Assert.NotNull(fromW1);
        Assert.NotNull(fromW2);
        Assert.Equal(inW1.Id, fromW1.Id);
        Assert.Equal(inW2.Id, fromW2.Id);
    }

    // --- Empty-id guards -------------------------------------------------------

    [Fact]
    public async Task FindById_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

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
    public async Task FindByKey_rejects_empty_ids_and_a_malformed_key()
    {
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByKeyAsync(
                Guid.Empty, Guid.CreateVersion7(), "type-alpha", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByKeyAsync(
                Guid.CreateVersion7(), Guid.Empty, "type-alpha", CancellationToken.None));
        // A malformed key can never address a stored (always-canonical) key.
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByKeyAsync(
                Guid.CreateVersion7(), Guid.CreateVersion7(), "bad key!", CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListByWorkspaceAsync(
                Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    // --- Tenant / workspace isolation negatives (both directions) --------------

    [Fact]
    public async Task A_type_in_workspace_W1_is_not_returned_via_workspace_W2()
    {
        // Mandatory negative workspace test (threat T5): a type exists in workspace W1 only.
        // Looking it up by id under workspace W2 must return null even though both the type and
        // W2 exist.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        var foundInW1 = await repository.FindByIdAsync(
            organization.Id, workspace1.Id, inW1.Id, CancellationToken.None);
        var viaW2 = await repository.FindByIdAsync(
            organization.Id, workspace2.Id, inW1.Id, CancellationToken.None);

        Assert.NotNull(foundInW1);
        Assert.Equal(workspace1.Id, foundInW1.WorkspaceId);
        Assert.Null(viaW2);
    }

    [Fact]
    public async Task A_type_in_organization_A_is_never_resolved_via_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5; docs/06_AUTHORIZATION_MATRIX.md:
        // organization boundary checked before workspace boundary): a type exists in a workspace
        // owned by organization A. Looking the SAME workspace id and type id up under organization
        // B's id must return null even though the workspace id and type id are correct.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var inA = await SeedEntityTypeAsync(organizationA.Id, workspaceInA.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        var underA = await repository.FindByIdAsync(
            organizationA.Id, workspaceInA.Id, inA.Id, CancellationToken.None);
        var underB = await repository.FindByIdAsync(
            organizationB.Id, workspaceInA.Id, inA.Id, CancellationToken.None);

        Assert.NotNull(underA);
        Assert.Null(underB);
    }

    [Fact]
    public async Task FindByKey_does_not_cross_the_workspace_or_tenant_boundary()
    {
        // The by-key lookup is also tenant- and workspace-scoped: the same key under a foreign
        // workspace or tenant resolves to nothing (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspace1 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugB);
        await SeedEntityTypeAsync(organizationA.Id, workspace1.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        Assert.NotNull(await repository.FindByKeyAsync(
            organizationA.Id, workspace1.Id, "type-alpha", CancellationToken.None));
        // Wrong workspace.
        Assert.Null(await repository.FindByKeyAsync(
            organizationA.Id, workspace2.Id, "type-alpha", CancellationToken.None));
        // Wrong tenant.
        Assert.Null(await repository.FindByKeyAsync(
            organizationB.Id, workspace1.Id, "type-alpha", CancellationToken.None));
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_workspaces_types()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace1 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var workspace2 = await SeedWorkspaceAsync(organization.Id, _workspaceSlugB);
        var inW1 = await SeedEntityTypeAsync(organization.Id, workspace1.Id, "type-alpha");
        var inW2 = await SeedEntityTypeAsync(organization.Id, workspace2.Id, "type-beta");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        var w1Types = await repository.ListByWorkspaceAsync(
            organization.Id, workspace1.Id, CancellationToken.None);

        Assert.Equal(inW1.Id, Assert.Single(w1Types).Id);
        Assert.DoesNotContain(w1Types, entityType => entityType.Id == inW2.Id);
    }

    [Fact]
    public async Task ListByWorkspace_never_returns_another_tenants_types()
    {
        // The list is tenant-scoped: querying organization B's id for a workspace owned by
        // organization A returns nothing (threat T5; the organization boundary is checked before
        // the workspace boundary).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _workspaceSlugA);
        await SeedEntityTypeAsync(organizationA.Id, workspaceInA.Id, "type-alpha");

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        var underA = await repository.ListByWorkspaceAsync(
            organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var underB = await repository.ListByWorkspaceAsync(
            organizationB.Id, workspaceInA.Id, CancellationToken.None);

        Assert.Single(underA);
        Assert.Empty(underB);
    }

    // --- Foreign-key enforcement -----------------------------------------------

    [Fact]
    public async Task A_type_cannot_reference_a_workspace_that_does_not_exist()
    {
        // The workspace_id foreign key is enforced (PRAGMA foreign_keys = ON): a type for a
        // non-existent workspace is rejected, so a dangling type can never exist outside a real
        // workspace boundary (threat T5). The organization is real to isolate the workspace FK as
        // the failing constraint.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var ghost = EntityType.Create(
            organization.Id, Guid.CreateVersion7(), "type-alpha", "Name", _validSchema, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_type_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a type whose tenant does not exist is
        // rejected even when the workspace exists, so the row can never carry a tenant boundary
        // that is not a real organization (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var workspace = await SeedWorkspaceAsync(organization.Id, _workspaceSlugA);
        var ghost = EntityType.Create(
            Guid.CreateVersion7(), workspace.Id, "type-alpha", "Name", _validSchema, _createdAt);

        await using var context = CreateContext();
        var repository = new EntityTypeRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }
}
