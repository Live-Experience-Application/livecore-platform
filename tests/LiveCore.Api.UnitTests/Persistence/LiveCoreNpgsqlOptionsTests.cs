using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Tests for the shared Npgsql connection-resilience configuration (CORE-CONC-003,
/// <see cref="LiveCoreNpgsqlOptions"/>). They prove the two acceptance behaviours the story requires:
/// <list type="bullet">
///   <item>a <see cref="LiveCoreDbContext"/> configured for PostgreSQL through the shared options uses a
///   RETRYING execution strategy (rather than the default non-retrying one);</item>
///   <item>a SIMULATED transient failure is retried and then succeeds, so a routine database disruption does
///   not surface to the caller as an error.</item>
/// </list>
///
/// Both run fully offline — building the model and the execution strategy never opens a connection — so they
/// need no database server (the same offline posture as the migration model-check tests). The retry behaviour
/// is exercised against the SAME execution strategy the API host, the worker jobs and the migrations factory
/// all build, because every one of those passes <see cref="LiveCoreNpgsqlOptions.Configure"/> to
/// <c>UseNpgsql</c>.
/// </summary>
public class LiveCoreNpgsqlOptionsTests
{
    private static LiveCoreDbContext CreateNpgsqlContext()
    {
        // A credential-free local connection string; no connection is ever opened (no secret in source).
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql("Host=localhost;Database=livecore-resilience-check", LiveCoreNpgsqlOptions.Configure)
            .Options;
        return new LiveCoreDbContext(options);
    }

    [Fact]
    public void Configured_db_context_uses_a_retrying_execution_strategy()
    {
        using var context = CreateNpgsqlContext();

        var strategy = context.Database.CreateExecutionStrategy();

        Assert.True(
            strategy.RetriesOnFailure,
            "The Npgsql DbContext must use a retrying execution strategy so transient failures are retried (CORE-CONC-003).");
    }

    [Fact]
    public async Task Retrying_execution_strategy_retries_a_transient_failure_then_succeeds()
    {
        using var context = CreateNpgsqlContext();
        var strategy = context.Database.CreateExecutionStrategy();

        var attempts = 0;
        var result = await strategy.ExecuteAsync(async () =>
        {
            attempts++;
            if (attempts == 1)
            {
                // A transient connection failure of the kind a failover/restart/brief partition produces:
                // Npgsql's transient-error detector treats an NpgsqlException wrapping a network I/O error as
                // transient, so the retrying strategy re-runs the operation rather than surfacing the failure.
                throw new NpgsqlException(
                    "simulated transient connection failure",
                    new IOException("connection reset by peer"));
            }

            await Task.Yield();
            return "succeeded";
        });

        // The first attempt failed transiently and the second succeeded — the failure never reached the caller.
        Assert.Equal(2, attempts);
        Assert.Equal("succeeded", result);
    }

    [Fact]
    public async Task Retrying_execution_strategy_does_not_retry_a_non_transient_failure()
    {
        // A non-transient failure (here a plain InvalidOperationException standing in for a constraint
        // violation or a logic bug) must NOT be retried — it fails immediately, exactly once, so resilience
        // never masks a real error.
        using var context = CreateNpgsqlContext();
        var strategy = context.Database.CreateExecutionStrategy();

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => strategy.ExecuteAsync(async () =>
        {
            attempts++;
            await Task.Yield();
            throw new InvalidOperationException("not a transient failure");
        }));

        Assert.Equal(1, attempts);
    }
}
