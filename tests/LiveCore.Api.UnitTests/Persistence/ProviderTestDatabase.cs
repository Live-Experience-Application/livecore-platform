// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Provisions the database a <b>provider-divergent</b> unit/repository test runs
/// against, so the unit suite exercises provider-specific behavior on REAL
/// PostgreSQL — not only the default in-memory SQLite (CORE-TST-004).
///
/// <para>
/// The bulk of the unit/repository tests construct their own in-memory SQLite
/// database directly, which exercises the real model mapping and SQL translation
/// without any database server. SQLite and PostgreSQL agree on the relational
/// semantics those tests assert, but they diverge on a few provider-specific
/// behaviors (collation, case-sensitivity, JSON, and the optimistic-concurrency
/// token, which is the PostgreSQL system column <c>xmin</c> and is mapped ONLY on
/// the Npgsql provider — see <see cref="LiveCoreDbContext"/>). Until this story the
/// <c>dotnet test</c> CI step ran every unit test on SQLite, so that divergent
/// behavior was never exercised at the unit level on real PostgreSQL.
/// </para>
///
/// <para>
/// This helper mirrors the integration suite's
/// <c>PostgresTestDatabase</c>/<c>WorkspaceApiFactory</c> seam at the unit level.
/// When the environment selects PostgreSQL
/// (<see cref="IsPostgresConfigured"/> — the CI <c>unit-smoke-postgres</c> job sets
/// <c>LIVECORE_TEST_DB_PROVIDER=Postgres</c> and a
/// <c>LIVECORE_TEST_POSTGRES</c> admin connection string) it provisions a throwaway
/// per-test database whose schema is applied by the real, checked-in EF Core
/// migrations (<c>Database.Migrate()</c>) and points the context at it; otherwise it
/// falls back to a private in-memory SQLite database created from the model
/// (<c>EnsureCreated()</c>), so a default local <c>dotnet test</c> needs no database
/// server. The throwaway PostgreSQL database is dropped on dispose.
/// </para>
///
/// <para>
/// No credentials live in the repository: the admin connection string is read only
/// from the environment and points at a throwaway CI database server (threat T7,
/// <c>docs/07_SECURITY_THREAT_MODEL.md</c>).
/// </para>
/// </summary>
internal sealed class ProviderTestDatabase : IAsyncDisposable
{
    /// <summary>
    /// Environment variable selecting the PostgreSQL provider. Set to
    /// <c>Postgres</c> (case-insensitive) by the CI job; absent for local runs,
    /// which keep using SQLite. Shares the name the integration suite already uses
    /// so one CI setting drives both.
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

    // Each per-test database issues its queries sequentially, so a small pool is
    // ample and keeps the live connection count low on PostgreSQL's modest default
    // max_connections.
    private const int _maxPoolSize = 5;

    private readonly SqliteConnection? _sqliteConnection;
    private readonly string? _postgresConnectionString;

    private ProviderTestDatabase(
        bool isPostgres,
        DbContextOptions<LiveCoreDbContext> options,
        SqliteConnection? sqliteConnection,
        string? postgresConnectionString)
    {
        IsPostgres = isPostgres;
        Options = options;
        _sqliteConnection = sqliteConnection;
        _postgresConnectionString = postgresConnectionString;
    }

    /// <summary>
    /// <see langword="true"/> when this database is real PostgreSQL; otherwise it is
    /// the in-memory SQLite fallback. A provider-divergent test branches on this to
    /// assert the PostgreSQL-only behavior when running against PostgreSQL and the
    /// SQLite behavior otherwise.
    /// </summary>
    public bool IsPostgres { get; }

    /// <summary>The EF Core options every context in this test is built from.</summary>
    public DbContextOptions<LiveCoreDbContext> Options { get; }

    /// <summary>
    /// <see langword="true"/> when the environment selects PostgreSQL and supplies an
    /// admin connection string; otherwise the unit suite stays on SQLite.
    /// </summary>
    public static bool IsPostgresConfigured =>
        string.Equals(
            Environment.GetEnvironmentVariable(ProviderEnvironmentVariable),
            _providerValue,
            StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable));

    /// <summary>
    /// Provisions the test database: a throwaway, migration-schema PostgreSQL
    /// database when the environment selects PostgreSQL, otherwise a private
    /// in-memory SQLite database created from the model.
    /// </summary>
    public static ProviderTestDatabase Create()
    {
        if (IsPostgresConfigured)
        {
            var connectionString = CreateMigratedPostgresDatabase();
            var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new ProviderTestDatabase(true, options, null, connectionString);
        }

        // One open connection keeps the private in-memory database alive for the
        // whole test while every step still uses its own context, so reads genuinely
        // round-trip through the database (mirrors the repository tests' SQLite
        // setup).
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var sqliteOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(connection)
            .Options;

        using (var context = new LiveCoreDbContext(sqliteOptions))
        {
            context.Database.EnsureCreated();
        }

        return new ProviderTestDatabase(false, sqliteOptions, connection, null);
    }

    /// <summary>Creates a fresh context over this test's database.</summary>
    public LiveCoreDbContext CreateContext() => new(Options);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _sqliteConnection?.Dispose();

        if (_postgresConnectionString is not null)
        {
            DropPostgresDatabase(_postgresConnectionString);
        }

        return ValueTask.CompletedTask;
    }

    private static string CreateMigratedPostgresDatabase()
    {
        var name = $"livecore_ut_{Guid.NewGuid():N}";
        Execute($"CREATE DATABASE \"{name}\";");

        var connectionString = ConnectionStringFor(name);
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        using (var context = new LiveCoreDbContext(options))
        {
            // Apply the real, checked-in migration files to the new PostgreSQL
            // database so the schema is exactly what production runs (and the
            // provider-specific xmin concurrency token is in effect).
            context.Database.Migrate();
        }

        // Release the pooled connections opened while migrating so the drop on
        // dispose never blocks on a lingering connection.
        NpgsqlConnection.ClearAllPools();
        return connectionString;
    }

    private static void DropPostgresDatabase(string connectionString)
    {
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
            // Best-effort cleanup: leave the throwaway database for the ephemeral CI
            // server to discard rather than failing the test on a transient drop.
        }
    }

    private static string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString())
        {
            Database = databaseName,
            MaxPoolSize = _maxPoolSize,
        }.ConnectionString;

    private static string AdminConnectionString() =>
        Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{AdminConnectionEnvironmentVariable} is not set; the PostgreSQL unit suite is not configured.");

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(AdminConnectionString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
