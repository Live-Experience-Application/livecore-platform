using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="OrganizationRepository"/> (CORE-ID-003).
///
/// They run against an in-memory SQLite database so the real model mapping,
/// SQL translation and the unique <c>slug</c> index are exercised on every
/// test run without any database server or Docker. The behaviors under test
/// (exact equality lookups, unique index enforcement, full isolation between
/// tenants) are relational semantics shared with PostgreSQL; provider-specific
/// verification happens against PostgreSQL in the deployment pipeline
/// (livecore-deploy) and the isolation test story CORE-ID-006.
///
/// The negative cases below are the tenant-isolation story for the
/// organization model (threat T5 in docs/07_SECURITY_THREAT_MODEL.md): a
/// lookup by the identifier of one tenant must never return another tenant,
/// and no field of one organization is reachable through another's
/// identifier.
/// </summary>
public sealed class OrganizationRepositoryTests : IDisposable
{
    private const string _slug = "northwind-labs";
    private const string _name = "Northwind Labs";
    private const string _foreignSlug = "acme-co";
    private const string _foreignName = "Acme Co";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public OrganizationRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads
        // genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext() => new(_contextOptions);

    private async Task<Organization> SeedOrganizationAsync(string slug, string name)
    {
        var organization = Organization.Create(slug, name, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var result = await repository.AddAsync(organization, CancellationToken.None);
        Assert.Equal(OrganizationAddResult.Added, result);
        return organization;
    }

    [Fact]
    public async Task Organization_round_trips_through_the_database()
    {
        var seeded = await SeedOrganizationAsync(_slug, _name);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var loaded = await repository.FindByIdAsync(seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(_slug, loaded.Slug);
        Assert.Equal(_name, loaded.Name);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lookup_by_slug_returns_the_matching_organization()
    {
        var seeded = await SeedOrganizationAsync(_slug, _name);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var loaded = await repository.FindBySlugAsync(_slug, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(_slug, loaded.Slug);
    }

    [Fact]
    public async Task Lookup_by_slug_canonicalizes_the_argument()
    {
        // Callers may pass any casing/whitespace variant of a valid slug; the
        // repository canonicalizes before matching, so the stored canonical
        // row is still found.
        await SeedOrganizationAsync(_slug, _name);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var loaded = await repository.FindBySlugAsync("  NorthWind-Labs ", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(_slug, loaded.Slug);
    }

    [Fact]
    public async Task Lookup_by_unknown_id_returns_no_organization()
    {
        await SeedOrganizationAsync(_slug, _name);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var loaded = await repository.FindByIdAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_by_unknown_slug_returns_no_organization()
    {
        await SeedOrganizationAsync(_slug, _name);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        var loaded = await repository.FindBySlugAsync(_foreignSlug, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_by_empty_id_is_rejected()
    {
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData("a")] // too short
    [InlineData("under_score")] // disallowed character
    [InlineData("   ")]
    public async Task Lookup_by_invalid_slug_is_rejected(string invalidSlug)
    {
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindBySlugAsync(invalidSlug, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected_and_the_existing_organization_stays_unchanged()
    {
        // The unique index on slug is the database-level guarantee that a
        // second writer can never overwrite an existing tenant with its own
        // row (threat T5).
        var existing = await SeedOrganizationAsync(_slug, "First Tenant");
        var duplicate = Organization.Create(_slug, "Second Tenant", _createdAt);

        await using (var context = CreateContext())
        {
            var repository = new OrganizationRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(OrganizationAddResult.DuplicateSlug, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new OrganizationRepository(context);
            var loaded = await repository.FindBySlugAsync(_slug, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal("First Tenant", loaded.Name);
        }
    }

    [Fact]
    public async Task Two_organizations_are_fully_isolated()
    {
        // Negative foreign-tenant story for the organization model
        // (threat T5): looking up tenant A by tenant B's identifiers must
        // never return tenant A, and vice versa. No field of one tenant is
        // reachable through the other's id or slug.
        var first = await SeedOrganizationAsync(_slug, _name);
        var second = await SeedOrganizationAsync(_foreignSlug, _foreignName);

        Assert.NotEqual(first.Id, second.Id);

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);

        // Each identifier resolves only to its own tenant.
        var firstById = await repository.FindByIdAsync(first.Id, CancellationToken.None);
        var secondById = await repository.FindByIdAsync(second.Id, CancellationToken.None);
        var firstBySlug = await repository.FindBySlugAsync(_slug, CancellationToken.None);
        var secondBySlug = await repository.FindBySlugAsync(_foreignSlug, CancellationToken.None);

        Assert.NotNull(firstById);
        Assert.NotNull(secondById);
        Assert.NotNull(firstBySlug);
        Assert.NotNull(secondBySlug);

        Assert.Equal(first.Id, firstById.Id);
        Assert.Equal(_name, firstById.Name);
        Assert.Equal(second.Id, secondById.Id);
        Assert.Equal(_foreignName, secondById.Name);

        // Cross-identifier lookups never bridge the two tenants: tenant A's
        // slug never returns tenant B and tenant B's id never returns tenant
        // A.
        Assert.Equal(first.Id, firstBySlug.Id);
        Assert.NotEqual(second.Id, firstBySlug.Id);
        Assert.Equal(second.Id, secondBySlug.Id);
        Assert.NotEqual(first.Id, secondBySlug.Id);

        // No field of one tenant is reachable via the other's identifier.
        Assert.NotEqual(firstById.Name, secondById.Name);
        Assert.NotEqual(firstById.Slug, secondById.Slug);
    }

    [Fact]
    public async Task Lookup_does_not_match_a_stored_slug_that_differs_only_in_case()
    {
        // The domain model always stores a canonical (lower-case) slug, so to
        // exercise the database comparison itself this inserts a row with an
        // upper-case slug directly, bypassing canonicalization. The lookup
        // canonicalizes its argument to "northwind-labs"; a case-sensitive
        // comparison must NOT return the stored "NORTHWIND-LABS" row, so a
        // future bypass of canonicalization can never let one tenant be
        // reached through another's slug in a different case (threat T5).
        // SQLite's default BINARY collation enforces this here; PostgreSQL
        // relies on its deterministic default collation, pinned in CORE-ID-006.
        await using (var seedContext = CreateContext())
        {
            await seedContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO organizations (id, slug, name, created_at, updated_at) VALUES ({0}, {1}, {2}, {3}, {3})",
                Guid.CreateVersion7().ToString(),
                _slug.ToUpperInvariant(),
                _name,
                _createdAt.UtcDateTime.ToString("o"));
        }

        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);

        var loaded = await repository.FindBySlugAsync(_slug, CancellationToken.None);

        Assert.Null(loaded);
    }
}
