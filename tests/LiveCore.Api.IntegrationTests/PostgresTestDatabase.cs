using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Provisions the per-test PostgreSQL databases the integration suite runs against
/// in CI (CORE-OPS-002).
///
/// By default the integration tests use the EF Core SQLite provider with
/// <c>EnsureCreated()</c> (see <see cref="WorkspaceApiFactory"/>), which builds the
/// schema straight from the model and therefore never exercises the checked-in
/// migration files. That hides any broken or drifted PostgreSQL migration. When the
/// <see cref="ProviderEnvironmentVariable"/>/<see cref="AdminConnectionEnvironmentVariable"/>
/// environment variables select PostgreSQL (the CI <c>integration-postgres</c> job),
/// this helper instead:
///
/// <list type="number">
///   <item>builds a <b>migrated template</b> database exactly once per test run by
///   applying the real, checked-in EF Core migrations
///   (<c>Database.Migrate()</c>) to a real PostgreSQL database — the behavior the
///   SQLite path never covers;</item>
///   <item>gives every <see cref="WorkspaceApiFactory"/> its own throwaway database
///   created as a fast file-level copy of that template
///   (<c>CREATE DATABASE ... TEMPLATE</c>), preserving the existing per-factory
///   isolation without re-running the migrations hundreds of times;</item>
///   <item>drops each throwaway database when its factory is disposed.</item>
/// </list>
///
/// No credentials live in the repository: the admin connection string is read only
/// from the environment and points at a throwaway CI database server (threat T7,
/// <c>docs/07_SECURITY_THREAT_MODEL.md</c>).
/// </summary>
internal static class PostgresTestDatabase
{
    /// <summary>
    /// Environment variable selecting the PostgreSQL provider for the integration
    /// suite. Set to <c>Postgres</c> (case-insensitive) by the CI job; absent for
    /// local runs, which keep using SQLite.
    /// </summary>
    public const string ProviderEnvironmentVariable = "LIVECORE_TEST_DB_PROVIDER";

    /// <summary>
    /// Environment variable carrying the admin/maintenance connection string (for
    /// example pointing at the server's <c>postgres</c> database). The per-test
    /// databases are created and dropped against this server. No credential is
    /// committed; it is supplied out of band for an ephemeral CI database only.
    /// </summary>
    public const string AdminConnectionEnvironmentVariable = "LIVECORE_TEST_POSTGRES";

    private const string _providerValue = "Postgres";
    private const string _templateDatabaseName = "livecore_it_template";

    // Keep each per-test database's connection pool small: several test databases
    // are alive at once while the test collections run in parallel (parallelism is
    // bounded by xunit.runner.json), and PostgreSQL's default max_connections is
    // modest. A test issues its HTTP calls sequentially, so a handful of pooled
    // connections is ample.
    private const int _maxPoolSize = 5;

    // The migrated template is built exactly once per test run (the first factory
    // that needs a schema-ready database); every per-test database is then a fast
    // copy of it, so the real migrations are applied to real PostgreSQL once rather
    // than once per test. The Lazy serializes the build; the copies and drops then
    // run concurrently with the parallel test collections.
    private static readonly Lazy<string> _template =
        new(BuildMigratedTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// <see langword="true"/> when the environment selects PostgreSQL and supplies
    /// an admin connection string; otherwise the integration suite stays on SQLite.
    /// </summary>
    public static bool IsConfigured =>
        string.Equals(
            Environment.GetEnvironmentVariable(ProviderEnvironmentVariable),
            _providerValue,
            StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable));

    /// <summary>
    /// Creates a fresh, schema-ready database as a copy of the migrated template and
    /// returns its connection string. The schema is exactly what the real migrations
    /// produce on PostgreSQL.
    /// </summary>
    public static string CreateMigratedDatabase()
    {
        var template = _template.Value;
        var name = NewDatabaseName();
        Execute($"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\";");
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// Creates a fresh, <b>empty</b> database (no schema applied) and returns its
    /// connection string. Used by the startup-migration guard, which must prove the
    /// host's own startup creates none of the schema.
    /// </summary>
    public static string CreateEmptyDatabase()
    {
        var name = NewDatabaseName();
        Execute($"CREATE DATABASE \"{name}\";");
        return ConnectionStringFor(name);
    }

    /// <summary>
    /// Drops the throwaway database named by <paramref name="connectionString"/>,
    /// terminating any lingering connections so the drop never blocks. It is
    /// best-effort: a failed drop never fails a test (the CI database server is
    /// ephemeral and discarded with the job), it only leaves one extra throwaway
    /// database behind. The caller drops only after the host has been torn down, so
    /// no application connection is holding the database open.
    /// </summary>
    public static void DropDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? throw new InvalidOperationException("The connection string carries no database name.");

        using (var pooled = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        try
        {
            Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);");
        }
        catch (NpgsqlException)
        {
            // Best-effort cleanup: leave the throwaway database for the ephemeral
            // server to discard rather than failing the test on a transient drop.
        }
    }

    private static string BuildMigratedTemplate()
    {
        // Start from a clean template (a previous run on a reused server might have
        // left one behind; on the ephemeral CI server it never exists yet).
        Execute($"DROP DATABASE IF EXISTS \"{_templateDatabaseName}\" WITH (FORCE);");
        Execute($"CREATE DATABASE \"{_templateDatabaseName}\";");

        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(ConnectionStringFor(_templateDatabaseName))
            .Options;

        using (var context = new LiveCoreDbContext(options))
        {
            // Apply the real, checked-in migration files to a real PostgreSQL
            // database. This is the coverage the SQLite EnsureCreated() path lacks:
            // a broken or drifted migration fails here.
            context.Database.Migrate();
        }

        // Release the template's pooled connections so PostgreSQL can use it as the
        // source of CREATE DATABASE ... TEMPLATE copies.
        NpgsqlConnection.ClearAllPools();

        return _templateDatabaseName;
    }

    private static string NewDatabaseName() => $"livecore_it_{Guid.NewGuid():N}";

    private static string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString())
        {
            Database = databaseName,
            MaxPoolSize = _maxPoolSize,
        }.ConnectionString;

    private static string AdminConnectionString() =>
        Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{AdminConnectionEnvironmentVariable} is not set; the PostgreSQL integration suite is not configured.");

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(AdminConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
