using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// The concurrency proof for the atomic quota check-and-consume (CORE-CONC-004) — the story's required
/// "N parallel session/start (or workspace/create) at a limit of 1 yield exactly 1 success and N-1 quota-exceeded".
///
/// It runs N consumers of the SAME quota TRULY in parallel, each on its own <see cref="LiveCoreDbContext"/> over its
/// own connection to ONE shared database (a temp-file SQLite database, so multiple writers serialize through SQLite's
/// normal busy-retry locking — the realistic stand-in for the production PostgreSQL row lock; the EF Npgsql
/// integration job exercises the same path against real PostgreSQL). With the limit at one, the single atomic
/// limit-guarded increment lets exactly one consumer through and rejects the rest, and the recorded usage never
/// exceeds the cap. The OLD separate check-then-record would let several consumers read <c>used &lt; limit</c> and
/// all record, overrunning the cap — exactly the time-of-check-to-time-of-use race this story closes. All fixtures
/// are generic Core keys (AGENTS.md).
/// </summary>
public sealed class QuotaEnforcementConcurrencyTests : IDisposable
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;
    private readonly TimeProvider _time = new FixedTimeProvider(_seedTime);

    public QuotaEnforcementConcurrencyTests()
    {
        // A temp-file database (not shared-cache in-memory) so independent connections contend through SQLite's
        // normal file locking, which retries on a busy writer rather than failing — the behavior a true parallel
        // consume needs.
        _databasePath = Path.Combine(Path.GetTempPath(), $"livecore-quota-concurrency-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath}";
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        // Release pooled connections so the temp-file handle is closed before the file is removed.
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task N_parallel_consumers_at_limit_one_yield_exactly_one_success(int parallelism)
    {
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("session.active.max", subjectId, limit: 1);

        // Fire all consumers at once and let them race for the single slot.
        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => Task.Run(ConsumeOnceAsync))
            .ToArray();
        var allowed = await Task.WhenAll(tasks);

        Assert.Equal(1, allowed.Count(success => success));            // exactly one success
        Assert.Equal(parallelism - 1, allowed.Count(success => !success)); // the rest are quota-exceeded
        Assert.Equal(1, await ReadUsageAsync(subjectId, quotaId));     // the cap was never exceeded

        async Task<bool> ConsumeOnceAsync()
        {
            await using var context = new LiveCoreDbContext(_contextOptions);
            var service = new QuotaEnforcementService(
                new QuotaDefinitionRepository(context),
                new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
                new QuotaUsageRepository(context),
                _time);
            var decision = await service.TryConsumeAsync(
                EntitlementSubjectType.Workspace, subjectId, "session.active.max", 1, CancellationToken.None);
            return decision.IsAllowed;
        }
    }

    [Fact]
    public async Task Parallel_consumers_under_a_higher_limit_admit_exactly_the_remaining_slots()
    {
        // Eight consumers race for three free slots: exactly three succeed and five are quota-exceeded, and the
        // recorded usage lands precisely at the cap — never above it.
        const int parallelism = 8;
        const long limit = 3;
        var subjectId = Guid.CreateVersion7();
        var quotaId = await SeedQuotaAsync("session.active.max", subjectId, limit);

        var tasks = Enumerable.Range(0, parallelism)
            .Select(_ => Task.Run(async () =>
            {
                await using var context = new LiveCoreDbContext(_contextOptions);
                var service = new QuotaEnforcementService(
                    new QuotaDefinitionRepository(context),
                    new SubjectEntitlementResolver(new SubjectEntitlementRepository(context)),
                    new QuotaUsageRepository(context),
                    _time);
                return (await service.TryConsumeAsync(
                    EntitlementSubjectType.Workspace, subjectId, "session.active.max", 1, CancellationToken.None))
                    .IsAllowed;
            }))
            .ToArray();
        var allowed = await Task.WhenAll(tasks);

        Assert.Equal(limit, allowed.Count(success => success));
        Assert.Equal(parallelism - limit, allowed.Count(success => !success));
        Assert.Equal(limit, await ReadUsageAsync(subjectId, quotaId));
    }

    private async Task<Guid> SeedQuotaAsync(string key, Guid subjectId, long? limit)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var entitlement = EntitlementDefinition.Define(key, EntitlementValueKind.Quota, "Quota", null, _seedTime);
        context.EntitlementDefinitions.Add(entitlement);
        var quota = QuotaDefinition.Define(entitlement, EntitlementSubjectType.Workspace, QuotaUnit.Count, _seedTime);
        context.QuotaDefinitions.Add(quota);
        context.SubjectEntitlements.Add(
            SubjectEntitlement.GrantQuota(EntitlementSubjectType.Workspace, subjectId, entitlement, limit, null, _seedTime));
        await context.SaveChangesAsync();
        return quota.Id;
    }

    private async Task<long> ReadUsageAsync(Guid subjectId, Guid quotaDefinitionId)
    {
        await using var context = new LiveCoreDbContext(_contextOptions);
        var usage = await new QuotaUsageRepository(context).FindBySubjectAndQuotaAsync(
            EntitlementSubjectType.Workspace, subjectId, quotaDefinitionId, CancellationToken.None);
        return usage?.UsedAmount ?? 0;
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the recorded usage timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
