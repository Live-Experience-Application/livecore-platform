using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="EntitlementDefinitionRepository"/> (CORE-ENTL-001).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>),
/// so the real model mapping, the SQL translation, the unique <c>entitlement_definitions(key)</c> index and the
/// value-kind string conversion are exercised on every run without any database server. The behaviors under
/// test (round-trip, scoped key/id lookups, deterministic catalog listing and provider-neutral duplicate-key
/// detection) are relational semantics shared with PostgreSQL; provider-specific verification happens against
/// PostgreSQL in the deployment pipeline.
///
/// The entitlement definition is a GLOBAL catalog (no organization_id), so there is no foreign-tenant /
/// unauthorized-role axis here as there is for a tenant-scoped aggregate (and no HTTP route in this story); the
/// fail-closed negative cases that matter for a definition catalog are the rejected duplicate key, the rejected
/// empty id and the rejected malformed key, all asserted below. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class EntitlementDefinitionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntitlementDefinitionRepositoryTests()
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

    private async Task<EntitlementDefinition> SeedAsync(EntitlementDefinition definition)
    {
        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        Assert.Equal(
            EntitlementDefinitionAddResult.Added,
            await repository.AddAsync(definition, CancellationToken.None));
        return definition;
    }

    [Fact]
    public async Task Definition_round_trips_through_the_database()
    {
        var seeded = await SeedAsync(EntitlementDefinition.Define(
            "workspace.active.max", EntitlementValueKind.Quota, "Active workspace limit", "Concurrent workspaces.", _createdAt));

        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        var loaded = await repository.FindByIdAsync(seeded.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal("workspace.active.max", loaded.Key);
        Assert.Equal(EntitlementValueKind.Quota, loaded.ValueKind);
        Assert.Equal("Active workspace limit", loaded.DisplayName);
        Assert.Equal("Concurrent workspaces.", loaded.Description);
        Assert.True(loaded.IsActive);
        Assert.Equal(_createdAt, loaded.CreatedAt);
    }

    [Fact]
    public async Task FindByKey_returns_the_definition_with_the_canonical_key()
    {
        var seeded = await SeedAsync(EntitlementDefinition.Define(
            "ads.disabled", EntitlementValueKind.Flag, "Ad-free experience", null, _createdAt));

        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);

        // The lookup canonicalizes the key first, so a differently-cased input resolves the same row.
        var loaded = await repository.FindByKeyAsync("ADS.DISABLED", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Null(loaded.Description);
    }

    [Fact]
    public async Task FindByKey_for_a_missing_key_returns_null()
    {
        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);

        var loaded = await repository.FindByKeyAsync("export.enabled", CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task ListAll_returns_the_catalog_in_key_order()
    {
        await SeedAsync(EntitlementDefinition.Define("workspace.active.max", EntitlementValueKind.Quota, "Workspaces", null, _createdAt));
        await SeedAsync(EntitlementDefinition.Define("ads.disabled", EntitlementValueKind.Flag, "Ad-free", null, _createdAt));
        await SeedAsync(EntitlementDefinition.Define("session.participant.max", EntitlementValueKind.Quota, "Participants", null, _createdAt));

        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        var all = await repository.ListAllAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "ads.disabled", "session.participant.max", "workspace.active.max" },
            all.Select(definition => definition.Key).ToArray());
    }

    [Fact]
    public async Task Adding_a_duplicate_key_is_rejected()
    {
        // The unique entitlement_definitions(key) index is the catalog integrity guarantee: a second definition
        // with the same key fails closed as DuplicateKey, never a second catalog entry for the same key.
        await SeedAsync(EntitlementDefinition.Define(
            "ads.disabled", EntitlementValueKind.Flag, "Ad-free experience", null, _createdAt));

        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        var duplicate = EntitlementDefinition.Define(
            "ads.disabled", EntitlementValueKind.Flag, "A different display name", null, _createdAt);

        var result = await repository.AddAsync(duplicate, CancellationToken.None);

        Assert.Equal(EntitlementDefinitionAddResult.DuplicateKey, result);
        Assert.Single(await repository.ListAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_deactivated_definition_round_trips_as_inactive()
    {
        var definition = EntitlementDefinition.Define(
            "export.enabled", EntitlementValueKind.Flag, "Export", null, _createdAt);
        definition.Deactivate(_createdAt.AddHours(1));
        await SeedAsync(definition);

        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);
        var loaded = await repository.FindByKeyAsync("export.enabled", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsActive);
    }

    [Fact]
    public async Task FindById_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByIdAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task FindByKey_rejects_a_malformed_key()
    {
        await using var context = CreateContext();
        var repository = new EntitlementDefinitionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByKeyAsync("not a valid key", CancellationToken.None));
    }
}
