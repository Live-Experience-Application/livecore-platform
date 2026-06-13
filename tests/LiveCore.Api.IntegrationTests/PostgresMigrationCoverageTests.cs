using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Proves the migration-coverage half of CORE-OPS-002: when the integration suite
/// runs against PostgreSQL (the CI <c>integration-postgres</c> job), each test's
/// database schema is applied by the <b>real, checked-in EF Core migrations</b>
/// rather than the SQLite <c>EnsureCreated()</c> schema that bypasses the migration
/// files. A broken or drifted migration therefore fails the suite instead of
/// staying invisible.
/// </summary>
public class PostgresMigrationCoverageTests
{
    [Fact]
    public async Task The_integration_database_schema_comes_from_the_real_migrations()
    {
        await using var factory = new WorkspaceApiFactory();

        // Creating the client builds the host and provisions the test database.
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        if (PostgresTestDatabase.IsConfigured)
        {
            // The per-test PostgreSQL database was provisioned from the migrated
            // template (Database.Migrate() applied the real migration files), so the
            // migrations are recorded as applied and none remain pending — coverage
            // the SQLite EnsureCreated() path can never provide.
            Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        else
        {
            // Default local run: the in-memory SQLite schema comes from
            // EnsureCreated(), which records no migrations. The real-migration
            // coverage above runs in CI against PostgreSQL.
            Assert.True(context.Database.IsSqlite());
        }
    }
}
