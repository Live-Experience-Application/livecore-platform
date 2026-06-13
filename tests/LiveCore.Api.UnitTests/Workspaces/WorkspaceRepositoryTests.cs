using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Workspaces;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="WorkspaceRepository"/> (CORE-WS-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL
/// translation, the (organization_id, slug) unique index and the
/// organization foreign key are exercised on every test run without any
/// database server or Docker. The behaviors under test (tenant-scoped equality
/// lookups, per-tenant unique-slug enforcement, full isolation between tenants,
/// FK enforcement) are relational semantics shared with PostgreSQL;
/// provider-specific verification happens against PostgreSQL in the deployment
/// pipeline (livecore-deploy).
///
/// The epic acceptance for Workspaces is "Workspace operations are generic and
/// authorized" with required authorization negative tests. At the aggregate +
/// repository level (no HTTP endpoints yet — those are CORE-WS-003), the
/// authorization-relevant property proven here is TENANT ISOLATION (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization): a workspace belongs to exactly one organization; a lookup
/// scoped to organization B never returns organization A's workspace (by id or
/// by slug); the same slug may exist in two different organizations but never
/// twice in one; and a workspace cannot be created for a non-existent
/// organization.
/// </summary>
public sealed class WorkspaceRepositoryTests : IDisposable
{
    private const string _slug = "summer-show";
    private const string _name = "Summer Show";
    private const string _foreignSlug = "winter-show";
    private const string _foreignName = "Winter Show";

    private const string _organizationSlugA = "northwind-labs";
    private const string _organizationSlugB = "acme-co";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public WorkspaceRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads genuinely
        // round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement on
        // so the organization FK in the model is genuinely exercised.
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

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug, string name)
    {
        var workspace = Workspace.Create(organizationId, slug, name, _createdAt);
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        Assert.Equal(WorkspaceAddResult.Added, await repository.AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    [Fact]
    public async Task Workspace_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var seeded = await SeedWorkspaceAsync(organization.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        var loaded = await repository.FindByIdAsync(organization.Id, seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(_slug, loaded.Slug);
        Assert.Equal(_name, loaded.Name);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lookup_by_slug_returns_the_matching_workspace_within_the_organization()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var seeded = await SeedWorkspaceAsync(organization.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        var loaded = await repository.FindBySlugAsync(organization.Id, _slug, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(_slug, loaded.Slug);
    }

    [Fact]
    public async Task Lookup_by_slug_canonicalizes_the_argument()
    {
        // Callers may pass any casing/whitespace variant of a valid slug; the
        // repository canonicalizes before matching, so the stored canonical row
        // is still found.
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        await SeedWorkspaceAsync(organization.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        var loaded = await repository.FindBySlugAsync(organization.Id, "  Summer-Show ", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(_slug, loaded.Slug);
    }

    [Fact]
    public async Task Lookup_by_unknown_id_returns_no_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        await SeedWorkspaceAsync(organization.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        var loaded = await repository.FindByIdAsync(
            organization.Id, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_by_unknown_slug_returns_no_workspace()
    {
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        await SeedWorkspaceAsync(organization.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);
        var loaded = await repository.FindBySlugAsync(organization.Id, _foreignSlug, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_by_empty_organization_id_is_rejected()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindBySlugAsync(Guid.Empty, _slug, CancellationToken.None));
    }

    [Fact]
    public async Task Lookup_by_empty_workspace_id_is_rejected()
    {
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData("a")] // too short
    [InlineData("under_score")] // disallowed character
    [InlineData("   ")]
    public async Task Lookup_by_invalid_slug_is_rejected(string invalidSlug)
    {
        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindBySlugAsync(Guid.CreateVersion7(), invalidSlug, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_slug_in_the_same_organization_is_rejected_and_the_existing_workspace_stays_unchanged()
    {
        // The unique (organization_id, slug) index is the database-level
        // guarantee that a second writer can never overwrite an existing
        // workspace with its own row inside one tenant (threat T5).
        var organization = await SeedOrganizationAsync(_organizationSlugA);
        var existing = await SeedWorkspaceAsync(organization.Id, _slug, "First Workspace");
        var duplicate = Workspace.Create(organization.Id, _slug, "Second Workspace", _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(WorkspaceAddResult.DuplicateSlug, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new WorkspaceRepository(context);
            var loaded = await repository.FindBySlugAsync(organization.Id, _slug, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal("First Workspace", loaded.Name);
        }
    }

    [Fact]
    public async Task The_same_slug_can_exist_in_two_different_organizations()
    {
        // Per-tenant (not global) uniqueness: the slug is the natural key WITHIN
        // one organization only. Two organizations may both own a workspace with
        // the same slug, and each is fully isolated from the other (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);

        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _slug, "Show in A");
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _slug, "Show in B");

        Assert.NotEqual(workspaceInA.Id, workspaceInB.Id);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        var loadedFromA = await repository.FindBySlugAsync(organizationA.Id, _slug, CancellationToken.None);
        var loadedFromB = await repository.FindBySlugAsync(organizationB.Id, _slug, CancellationToken.None);

        Assert.NotNull(loadedFromA);
        Assert.NotNull(loadedFromB);
        // Each organization's slug lookup returns only its own workspace.
        Assert.Equal(workspaceInA.Id, loadedFromA.Id);
        Assert.Equal("Show in A", loadedFromA.Name);
        Assert.Equal(workspaceInB.Id, loadedFromB.Id);
        Assert.Equal("Show in B", loadedFromB.Name);
    }

    [Fact]
    public async Task A_workspace_is_never_returned_through_a_foreign_organizations_id()
    {
        // Negative authorization story for the workspace aggregate (threat
        // T5/T1): a workspace created in organization A must never be reachable
        // through organization B's id, by its surrogate id OR by its slug, even
        // though the id/slug values are correct. Object-level authorization is
        // tenant-scoped: the wrong tenant denies access.
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);
        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _slug, _name);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        // Correct tenant: the workspace is found by both keys.
        Assert.NotNull(await repository.FindByIdAsync(organizationA.Id, workspaceInA.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindBySlugAsync(organizationA.Id, _slug, CancellationToken.None));

        // Foreign tenant (B): the same id and the same slug return nothing.
        Assert.Null(await repository.FindByIdAsync(organizationB.Id, workspaceInA.Id, CancellationToken.None));
        Assert.Null(await repository.FindBySlugAsync(organizationB.Id, _slug, CancellationToken.None));
    }

    [Fact]
    public async Task Two_organizations_workspaces_are_fully_isolated()
    {
        // Each organization owns a distinct workspace; neither tenant's
        // identifiers ever resolve the other tenant's workspace, in either
        // direction. No field of one tenant's workspace is reachable through the
        // other tenant's id (threat T5).
        var organizationA = await SeedOrganizationAsync(_organizationSlugA);
        var organizationB = await SeedOrganizationAsync(_organizationSlugB);

        var workspaceInA = await SeedWorkspaceAsync(organizationA.Id, _slug, _name);
        var workspaceInB = await SeedWorkspaceAsync(organizationB.Id, _foreignSlug, _foreignName);

        Assert.NotEqual(workspaceInA.Id, workspaceInB.Id);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        // Each tenant resolves only its own workspace.
        var aById = await repository.FindByIdAsync(organizationA.Id, workspaceInA.Id, CancellationToken.None);
        var bById = await repository.FindByIdAsync(organizationB.Id, workspaceInB.Id, CancellationToken.None);
        Assert.NotNull(aById);
        Assert.NotNull(bById);
        Assert.Equal(_name, aById.Name);
        Assert.Equal(_foreignName, bById.Name);

        // Cross-tenant lookups never bridge the two: A's workspace id under B is
        // null, and B's workspace id under A is null; A's slug under B is null,
        // and B's slug under A is null.
        Assert.Null(await repository.FindByIdAsync(organizationB.Id, workspaceInA.Id, CancellationToken.None));
        Assert.Null(await repository.FindByIdAsync(organizationA.Id, workspaceInB.Id, CancellationToken.None));
        Assert.Null(await repository.FindBySlugAsync(organizationB.Id, _slug, CancellationToken.None));
        Assert.Null(await repository.FindBySlugAsync(organizationA.Id, _foreignSlug, CancellationToken.None));
    }

    [Fact]
    public async Task A_workspace_cannot_reference_an_organization_that_does_not_exist()
    {
        // The organization_id foreign key is enforced (PRAGMA foreign_keys = ON):
        // a workspace for a non-existent tenant is rejected, so a dangling
        // workspace can never exist outside a real tenant boundary (threat T5).
        var ghost = Workspace.Create(Guid.CreateVersion7(), _slug, _name, _createdAt);

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => repository.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task Lookup_does_not_match_a_stored_slug_that_differs_only_in_case()
    {
        // The domain model always stores a canonical (lower-case) slug, so to
        // exercise the database comparison itself this inserts a row with an
        // upper-case slug directly, bypassing canonicalization. The lookup
        // canonicalizes its argument to "summer-show"; a case-sensitive
        // comparison must NOT return the stored "SUMMER-SHOW" row, so a future
        // bypass of canonicalization can never let a workspace be reached through
        // a slug in a different case (threat T5). SQLite's default BINARY
        // collation enforces this here; PostgreSQL relies on its deterministic
        // default collation.
        var organization = await SeedOrganizationAsync(_organizationSlugA);

        await using (var seedContext = CreateContext())
        {
            // Pass the ids as Guid parameters (not strings) so the provider
            // serializes them with the SAME conversion the organizations row
            // used; otherwise the organization foreign key would not match.
            await seedContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO workspaces (id, organization_id, slug, name, status, created_at, updated_at) "
                    + "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {5})",
                Guid.CreateVersion7(),
                organization.Id,
                _slug.ToUpperInvariant(),
                _name,
                nameof(WorkspaceStatus.Active),
                _createdAt.UtcDateTime.ToString("o"));
        }

        await using var context = CreateContext();
        var repository = new WorkspaceRepository(context);

        var loaded = await repository.FindBySlugAsync(organization.Id, _slug, CancellationToken.None);

        Assert.Null(loaded);
    }
}
