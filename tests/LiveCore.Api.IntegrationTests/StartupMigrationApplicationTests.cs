using System.Data.Common;
using System.Net;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Guards the deployment-safety half of the migration application path
/// (CORE-OPS-001): the schema is applied by a separate deployment step (the
/// migrations bundle / runner image), and the API host <b>never</b> implicitly
/// migrates or creates the schema on startup. Implicit startup migration is
/// unsafe for a multi-instance rollout (every replica would race to migrate), so
/// this test fails loudly if a future change wires a startup
/// <c>Migrate()</c>/<c>EnsureCreated()</c> into the host.
/// </summary>
public class StartupMigrationApplicationTests
{
    [Fact]
    public async Task Host_starts_and_serves_traffic_without_implicitly_creating_or_migrating_the_schema()
    {
        // Boot the real application with persistence configured over a fresh,
        // EMPTY database (the guard factory deliberately does not create the
        // schema). Production wiring is otherwise untouched.
        using var factory = new StartupMigrationGuardApiFactory();

        // Creating the client starts the host and runs its full startup pipeline.
        using var client = factory.CreateClient();
        var liveness = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        // Startup completed and the process serves HTTP: it did not block on (nor
        // perform) a migration against the empty database.
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);

        // The database is still empty: startup created and migrated nothing. If a
        // startup Migrate()/EnsureCreated() were introduced, application tables
        // (or the EF migrations history table) would exist here.
        Assert.Equal(0, await CountApplicationTablesAsync(factory));
    }

    private static async Task<long> CountApplicationTablesAsync(StartupMigrationGuardApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var result = await command.ExecuteScalarAsync();

        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A <see cref="WorkspaceApiFactory"/> that swaps in the same production
    /// persistence wiring but leaves the schema absent, so the only thing that
    /// could populate the database is the application's own startup — which must
    /// not.
    /// </summary>
    private sealed class StartupMigrationGuardApiFactory : WorkspaceApiFactory
    {
        protected override void InitializeDatabase(LiveCoreDbContext context)
        {
            // Intentionally do nothing: no EnsureCreated(), no Migrate(). The
            // schema stays empty so the assertion can prove the host's startup
            // created none of it.
        }
    }
}
