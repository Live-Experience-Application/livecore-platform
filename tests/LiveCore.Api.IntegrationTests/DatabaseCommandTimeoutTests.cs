// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Proves the CORE-RES-004 acceptance behaviour that needs a REAL database: a query that exceeds the configured
/// timeout is ABORTED, not left to hang. The runtime persistence registration
/// (<see cref="LiveCoreNpgsqlOptions.UseLiveCoreNpgsql"/>) bounds every command with a client-side
/// <c>CommandTimeout</c> and a server-side <c>statement_timeout</c>; this test points a context at a throwaway
/// PostgreSQL database, configures a SHORT statement timeout, runs a deliberately long <c>pg_sleep</c> and asserts
/// PostgreSQL aborts it (the <c>57014</c> "canceling statement due to statement timeout" condition) far sooner than
/// the sleep would finish — so the work is bounded and the retrying execution strategy does not amplify it.
///
/// <para>
/// It runs only when the suite is configured against real PostgreSQL
/// (<see cref="PostgresTestDatabase.IsConfigured"/>, the CI <c>integration-postgres</c> job): the timeout is a
/// genuine server behaviour with no SQLite equivalent (SQLite has no <c>statement_timeout</c> and no
/// <c>pg_sleep</c>), so on the default SQLite path the test no-ops rather than asserting a behaviour the provider
/// cannot exhibit — the same provider-gating the other PostgreSQL-only integration tests use. No credential lives
/// in source: the database is a throwaway provisioned from the environment-supplied admin connection (threat T7).
/// </para>
/// </summary>
public sealed class DatabaseCommandTimeoutTests
{
    [Fact]
    public async Task A_query_exceeding_the_statement_timeout_is_aborted_not_hung()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            // SQLite path: statement_timeout / pg_sleep do not exist, so there is nothing to assert. The real
            // behaviour is covered by the CI integration-postgres job.
            return;
        }

        var connectionString = PostgresTestDatabase.CreateEmptyDatabase();
        try
        {
            // A SHORT server-side ceiling (1s) and a generous client ceiling (60s) so the SERVER aborts the query
            // deterministically — the abort is a non-transient PostgreSQL cancellation, so the retrying execution
            // strategy does not retry it.
            var tuning = new LiveCorePersistenceOptions(
                commandTimeout: TimeSpan.FromSeconds(60),
                statementTimeout: TimeSpan.FromSeconds(1));

            var builder = new DbContextOptionsBuilder<LiveCoreDbContext>();
            builder.UseLiveCoreNpgsql(connectionString, tuning);

            await using var context = new LiveCoreDbContext(builder.Options);

            var stopwatch = Stopwatch.StartNew();

            // pg_sleep(20) would block for 20 seconds if nothing bounded it; the 1s statement_timeout must abort it.
            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => context.Database.ExecuteSqlRawAsync("SELECT pg_sleep(20)"));

            stopwatch.Stop();

            // It was ABORTED, not run to completion: the abort landed an order of magnitude before the sleep would
            // have finished (and well before the client CommandTimeout, and with no retry amplification).
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"The stuck query should have been aborted promptly, but it took {stopwatch.Elapsed}.");

            // And it was aborted by the SERVER-side statement_timeout specifically (SqlState 57014), not merely
            // hung or failed for another reason.
            var postgresException = FindPostgresException(exception);
            Assert.NotNull(postgresException);
            Assert.Equal(PostgresErrorCodes.QueryCanceled, postgresException!.SqlState);
        }
        finally
        {
            PostgresTestDatabase.DropDatabase(connectionString);
        }
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
