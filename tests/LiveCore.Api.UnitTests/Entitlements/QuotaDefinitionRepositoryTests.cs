using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="QuotaDefinitionRepository"/> (CORE-ENTL-003). They run
/// against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so the real
/// model mapping, the unique <c>entitlement_definition_id</c> index, the subject-type/unit string conversions, the
/// active-by-subject-type lookup and the restricted entitlement-definition foreign key are exercised on every run.
/// A quota definition is a GLOBAL catalog row (no tenant), so the cases that matter are the catalog reads, the
/// one-quota-per-entitlement guard and the active filter. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class QuotaDefinitionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public QuotaDefinitionRepositoryTests()
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

    private async Task<EntitlementDefinition> SeedEntitlementAsync(string key)
    {
        await using var context = CreateContext();
        var entitlement = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        await context.SaveChangesAsync();
        return entitlement;
    }

    [Fact]
    public async Task Definition_round_trips_through_the_database()
    {
        var entitlement = await SeedEntitlementAsync("asset.storage.bytes.max");
        var definition = QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Bytes, _seedTime);

        await using (var context = CreateContext())
        {
            var repository = new QuotaDefinitionRepository(context);
            Assert.Equal(QuotaDefinitionAddResult.Added, await repository.AddAsync(definition, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var repository = new QuotaDefinitionRepository(context);
            var loaded = await repository.FindByIdAsync(definition.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(entitlement.Id, loaded.EntitlementDefinitionId);
            Assert.Equal("asset.storage.bytes.max", loaded.EntitlementKey);
            Assert.Equal(EntitlementSubjectType.User, loaded.SubjectType);
            Assert.Equal(QuotaUnit.Bytes, loaded.Unit);
            Assert.True(loaded.IsActive);
        }
    }

    [Fact]
    public async Task A_second_quota_for_the_same_entitlement_is_rejected()
    {
        var entitlement = await SeedEntitlementAsync("workspace.active.max");

        await using var context = CreateContext();
        var repository = new QuotaDefinitionRepository(context);
        Assert.Equal(
            QuotaDefinitionAddResult.Added,
            await repository.AddAsync(
                QuotaDefinition.Define(entitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime),
                CancellationToken.None));

        var result = await repository.AddAsync(
            QuotaDefinition.Define(entitlement, EntitlementSubjectType.Workspace, QuotaUnit.Count, _seedTime),
            CancellationToken.None);

        Assert.Equal(QuotaDefinitionAddResult.DuplicateEntitlement, result);
    }

    [Fact]
    public async Task ListActiveBySubjectType_filters_by_subject_type_and_excludes_retired_definitions()
    {
        var userEntitlement = await SeedEntitlementAsync("workspace.active.max");
        var retiredEntitlement = await SeedEntitlementAsync("session.active.max");
        var workspaceEntitlement = await SeedEntitlementAsync("session.participant.max");

        await using var context = CreateContext();
        var repository = new QuotaDefinitionRepository(context);
        await repository.AddAsync(
            QuotaDefinition.Define(userEntitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime),
            CancellationToken.None);

        var retired = QuotaDefinition.Define(retiredEntitlement, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime);
        retired.Deactivate(_seedTime.AddHours(1));
        await repository.AddAsync(retired, CancellationToken.None);

        await repository.AddAsync(
            QuotaDefinition.Define(workspaceEntitlement, EntitlementSubjectType.Workspace, QuotaUnit.Count, _seedTime),
            CancellationToken.None);

        var userQuotas = await repository.ListActiveBySubjectTypeAsync(EntitlementSubjectType.User, CancellationToken.None);
        var workspaceQuotas = await repository.ListActiveBySubjectTypeAsync(EntitlementSubjectType.Workspace, CancellationToken.None);

        // Only the active user quota: the retired one is excluded, the workspace one is a different subject kind.
        Assert.Equal(new[] { "workspace.active.max" }, userQuotas.Select(q => q.EntitlementKey).ToArray());
        Assert.Equal(new[] { "session.participant.max" }, workspaceQuotas.Select(q => q.EntitlementKey).ToArray());
    }

    [Fact]
    public async Task Defining_a_quota_whose_entitlement_does_not_exist_is_rejected()
    {
        // The entitlement is built but NEVER persisted, so the restricted foreign key rejects the insert (and it is
        // not a duplicate, so it surfaces).
        var orphan = EntitlementDefinition.Define("workspace.active.max", EntitlementValueKind.Quota, "Quota", null, _seedTime);

        await using var context = CreateContext();
        var repository = new QuotaDefinitionRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(
            QuotaDefinition.Define(orphan, EntitlementSubjectType.User, QuotaUnit.Count, _seedTime),
            CancellationToken.None));
    }

    [Fact]
    public async Task FindById_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new QuotaDefinitionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByIdAsync(Guid.Empty, CancellationToken.None));
    }
}
