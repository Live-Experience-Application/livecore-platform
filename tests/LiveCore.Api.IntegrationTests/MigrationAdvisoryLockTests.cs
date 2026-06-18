// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Proves the deploy-safety behaviour of CORE-OPS-012 that needs a REAL database: the migration runner
/// (<see cref="LiveCoreMigrationRunner"/>) serialises concurrent invocations on a session-level PostgreSQL
/// advisory lock on a fixed app key, so two runners racing against one empty database cannot both attempt the
/// same migration <c>Up()</c> and corrupt the schema or the <c>__EFMigrationsHistory</c> table.
///
/// <para>
/// The two runner invocations are modelled as two concurrent <see cref="LiveCoreMigrationRunner.RunAsync"/>
/// calls (the in-process analogue of two migrate processes); completing without throwing is each process
/// "exiting 0". The tests run only when the suite is configured against real PostgreSQL
/// (<see cref="PostgresTestDatabase.IsConfigured"/>, the CI <c>integration-postgres</c> job): a session
/// advisory lock is a genuine PostgreSQL feature with no SQLite equivalent, so on the default SQLite path the
/// tests no-op rather than asserting a behaviour the provider cannot exhibit — the same provider-gating the
/// other PostgreSQL-only integration tests use. No credential lives in source: the database is a throwaway
/// provisioned from the environment-supplied admin connection (threat T7).
/// </para>
/// </summary>
public sealed class MigrationAdvisoryLockTests
{
    [Fact]
    public async Task Two_concurrent_runners_against_an_empty_database_apply_the_schema_exactly_once()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            // SQLite path: PostgreSQL session advisory locks do not exist, so there is nothing to assert. The
            // real concurrency behaviour is covered by the CI integration-postgres job.
            return;
        }

        var connectionString = PostgresTestDatabase.CreateEmptyDatabase();
        try
        {
            // Two runners launched simultaneously against the SAME empty database. The advisory lock serialises
            // them: one acquires it and applies the whole schema, the other blocks until it releases and then
            // finds the schema current and applies nothing. Both completing without throwing is both processes
            // exiting 0.
            await Task.WhenAll(
                LiveCoreMigrationRunner.RunAsync(connectionString),
                LiveCoreMigrationRunner.RunAsync(connectionString));

            // The schema was applied exactly once and the history is consistent: every defined migration is
            // recorded, none is pending, and __EFMigrationsHistory holds exactly one row per migration — no
            // duplicate the second runner could have inserted by re-applying an already-applied migration.
            var definedCount = await DefinedMigrationCountAsync(connectionString);
            Assert.NotEqual(0, definedCount);
            Assert.Empty(await PendingMigrationsAsync(connectionString));
            Assert.Equal(definedCount, await AppliedMigrationCountAsync(connectionString));

            var (historyRows, distinctHistoryRows) = await MigrationHistoryCountsAsync(connectionString);
            Assert.Equal(definedCount, historyRows);
            Assert.Equal(historyRows, distinctHistoryRows);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(connectionString);
        }
    }

    [Fact]
    public async Task A_second_runner_blocks_until_the_first_holder_releases_the_advisory_lock()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            return;
        }

        var connectionString = PostgresTestDatabase.CreateEmptyDatabase();
        using var cancellation = new CancellationTokenSource();
        Task? runner = null;
        NpgsqlConnection? holder = null;
        try
        {
            // Stand in for a first runner that already holds the lock: acquire the SAME fixed-key advisory lock
            // on a held connection, so the database is still empty but the lock is taken.
            holder = new NpgsqlConnection(connectionString);
            await holder.OpenAsync();
            await ExecuteAdvisoryAsync(holder, "SELECT pg_advisory_lock(@key);");

            // A runner started now must BLOCK on the lock — not error, not apply anything — while the lock is
            // held elsewhere.
            runner = LiveCoreMigrationRunner.RunAsync(connectionString, cancellation.Token);
            var settled = await Task.WhenAny(runner, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(runner, settled);
            Assert.False(runner.IsCompleted);

            // It is blocked BEFORE applying anything: the database still has no tables (not even
            // __EFMigrationsHistory), proving it is waiting on the lock rather than mid-apply.
            Assert.Equal(0, await PublicTableCountAsync(connectionString));

            // Release the lock; the runner now acquires it, applies the schema and completes cleanly (exit 0).
            await ExecuteAdvisoryAsync(holder, "SELECT pg_advisory_unlock(@key);");
            await runner.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Empty(await PendingMigrationsAsync(connectionString));
        }
        finally
        {
            // Ensure the (possibly still-blocked) runner is observed and torn down before the database is
            // dropped: cancelling aborts a blocked lock wait, and awaiting observes any resulting exception.
            cancellation.Cancel();
            if (runner is not null)
            {
                try
                {
                    await runner;
                }
                catch
                {
                    // A cancelled (still-blocked) or already-completed runner — observed, ignored.
                }
            }

            if (holder is not null)
            {
                await holder.DisposeAsync();
            }

            PostgresTestDatabase.DropDatabase(connectionString);
        }
    }

    private static LiveCoreDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new LiveCoreDbContext(options);
    }

    private static async Task<int> DefinedMigrationCountAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return context.Database.GetMigrations().Count();
    }

    private static async Task<int> AppliedMigrationCountAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return (await context.Database.GetAppliedMigrationsAsync()).Count();
    }

    private static async Task<IReadOnlyList<string>> PendingMigrationsAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        return (await context.Database.GetPendingMigrationsAsync()).ToList();
    }

    private static async Task<(long Rows, long Distinct)> MigrationHistoryCountsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*), count(DISTINCT \"MigrationId\") FROM \"__EFMigrationsHistory\";";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> PublicTableCountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAdvisoryAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("key", LiveCoreMigrationRunner.AdvisoryLockKey);
        await command.ExecuteNonQueryAsync();
    }
}
