// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration test for the database-schema readiness gate (CORE-OBS-010) over the real <c>Program</c> — the
/// story's required test. With persistence configured against a database one migration behind the build (the
/// state a deploy whose migrate step was skipped/failed, or an API image rolled forward ahead of its schema, is
/// in) it pins the acceptance criteria end-to-end against the real, checked-in migrations:
/// <list type="bullet">
///   <item><c>/health/ready</c> reports not-ready (<c>503</c>, status-only) while the database lacks a migration
///   the build expects, so the host leaves the rotation instead of advertising Ready and then 500-ing on the
///   first domain query;</item>
///   <item>liveness (<c>/health/live</c>) stays shallow and <c>200</c> — a stale schema never restarts an
///   otherwise-live process;</item>
///   <item>once the last migration is applied (out of band — the host never migrates itself, CORE-OPS-001),
///   readiness flips to ready.</item>
/// </list>
///
/// The real migration-history gate needs the checked-in PostgreSQL migrations and a real
/// <c>__EFMigrationsHistory</c> table, so this end-to-end flip runs in the <c>integration-postgres</c> CI job;
/// the default in-memory SQLite path records no migrations and cannot apply the Npgsql migration files. The
/// gating LOGIC (current => Ready, behind => not-ready, fail-closed, bounded) is covered provider-independently
/// by the <c>SchemaUpToDateHealthCheckTests</c> unit tests. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SchemaReadinessHealthCheckTests
{
    [Fact]
    public async Task Readiness_is_not_ready_until_the_schema_is_brought_up_to_date()
    {
        if (!PostgresTestDatabase.IsConfigured)
        {
            // Default local run: SQLite EnsureCreated() records no migrations and cannot run the Npgsql
            // migration files, so the schema-version flip is exercised in CI against real PostgreSQL. The
            // gating logic is covered provider-independently by the unit tests.
            Assert.False(PostgresTestDatabase.IsConfigured);
            return;
        }

        await using var factory = new SchemaBehindApiFactory();
        using var client = factory.CreateClient();

        // Sanity: the database really is behind the build (exactly the last migration is missing).
        Assert.NotEmpty(await PendingMigrationsAsync(factory));

        // The schema is behind the build, so the schema-readiness check reports not-ready while liveness stays
        // shallow and up.
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        // Status-only: which migration is missing never leaks to the unauthenticated endpoint (threat T7).
        Assert.Equal("""{"status":"Unhealthy"}""", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await live.Content.ReadAsStringAsync());

        // Apply the remaining (last) migration out of band — the host never migrates itself (CORE-OPS-001).
        await ApplyPendingMigrationsAsync(factory);
        Assert.Empty(await PendingMigrationsAsync(factory));

        // Readiness flips to ready now that the schema is current; liveness was never affected.
        var readyAfter = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var liveAfter = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, readyAfter.StatusCode);
        Assert.Equal("""{"status":"Healthy"}""", await readyAfter.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, liveAfter.StatusCode);
    }

    private static async Task<IReadOnlyList<string>> PendingMigrationsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return (await context.Database.GetPendingMigrationsAsync()).ToArray();
    }

    private static async Task ApplyPendingMigrationsAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> whose PostgreSQL database carries every migration except the last, so
    /// the schema is exactly one migration behind the build the host runs.
    /// </summary>
    private sealed class SchemaBehindApiFactory : WorkspaceApiFactory
    {
        protected override string CreatePostgresDatabase() =>
            PostgresTestDatabase.CreateDatabaseMigratedToAllButLast();
    }
}
