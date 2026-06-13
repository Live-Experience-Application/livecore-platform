using LiveCore.Api.Observability;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Tests for <see cref="DatabaseFailureMetricsInterceptor"/> (CORE-OBS-001), which counts failed database
/// commands (docs/15_OBSERVABILITY.md "database query failures"). They attach the interceptor to a real EF
/// Core <see cref="LiveCoreDbContext"/> over in-memory SQLite and execute a deliberately invalid command, so
/// the interceptor is exercised through the genuine EF Core command pipeline (both the sync and async failure
/// hooks), proving the metric increments on a real failure — while a successful command records nothing.
///
/// Shares the serialized "LiveCore metrics" collection so the listener sees only this test's measurements.
/// </summary>
[Collection(LiveCoreMetricsTestCollection.Name)]
public sealed class DatabaseFailureMetricsInterceptorTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DatabaseFailureMetricsInterceptorTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext(LiveCoreMetrics metrics)
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new DatabaseFailureMetricsInterceptor(metrics))
            .Options;
        var context = new LiveCoreDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task A_failed_async_command_increments_the_database_failure_counter()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        await using var context = CreateContext(metrics);

        // An invalid command (a non-existent table) fails in the EF Core command pipeline, firing the
        // interceptor's async failure hook.
        await Assert.ThrowsAsync<SqliteException>(
            () => context.Database.ExecuteSqlRawAsync("SELECT 1 FROM a_table_that_does_not_exist"));

        Assert.True(recorded.Count("livecore.database.failures") >= 1);
    }

    [Fact]
    public void A_failed_sync_command_increments_the_database_failure_counter()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        using var context = CreateContext(metrics);

        Assert.Throws<SqliteException>(
            () => context.Database.ExecuteSqlRaw("SELECT 1 FROM a_table_that_does_not_exist"));

        Assert.True(recorded.Count("livecore.database.failures") >= 1);
    }

    [Fact]
    public async Task A_successful_command_records_no_failure()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        await using var context = CreateContext(metrics);

        await context.Database.ExecuteSqlRawAsync("SELECT 1");

        Assert.Equal(0, recorded.Count("livecore.database.failures"));
    }
}
