// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for the workspace API
/// integration tests (CORE-WS-003). It boots the real application and exercises
/// the endpoints over real HTTP, swapping only two seams and never weakening
/// production behavior:
/// <list type="number">
///   <item>A configured connection string
///   (<c>ConnectionStrings:Database</c>) so the production persistence
///   conditional in <c>Program.cs</c> registers the repositories, the tenant
///   context resolver, the DbContext and the database readiness check — exactly
///   the production wiring.
///   <para>
///   By default the Npgsql provider is then replaced with EF Core SQLite (a
///   private in-memory database kept alive by one open connection, foreign keys
///   ON) so no PostgreSQL server is needed, mirroring the existing repository
///   tests' SQLite setup. When the environment selects PostgreSQL
///   (<see cref="PostgresTestDatabase.IsConfigured"/>, the CI
///   <c>integration-postgres</c> job, CORE-OPS-002) the factory instead points the
///   <b>unchanged</b> production Npgsql registration at a throwaway per-test
///   database whose schema was applied by the real, checked-in migrations — so the
///   suite covers the migration files, not only the SQLite EnsureCreated() schema.
///   </para></item>
///   <item>A test authentication scheme
///   (<see cref="TestAuthenticationHandler"/>) registered as the default scheme,
///   so <c>RequireAuthorization()</c> on the workspace route group authenticates
///   from request headers instead of a real identity provider. Production auth
///   wiring is untouched.</item>
/// </list>
///
/// The shared SQLite connection (or the dedicated PostgreSQL database) means every
/// request in a test sees the same seeded database. Use <see cref="SeedAsync"/> to
/// arrange data and the <c>Create*Client</c> helpers to act as a chosen caller.
///
/// It is unsealed so a test that needs to observe an extra production seam can
/// derive from it and override <see cref="ConfigureWebHost"/> (calling base
/// first); for example the realtime delivery tests substitute a recording
/// <see cref="LiveCore.Api.Realtime.IRealtimeBackplane"/> to capture which
/// server-managed groups an event was delivered to.
/// </summary>
internal class WorkspaceApiFactory : WebApplicationFactory<Program>
{
    // A placeholder connection string only switches the production persistence
    // conditional on; under SQLite the actual provider is replaced below, so no
    // real PostgreSQL connection is ever opened.
    private const string _placeholderConnectionString =
        "Host=localhost;Port=5432;Database=livecore-integration-test";

    private readonly bool _usePostgres = PostgresTestDatabase.IsConfigured;

    // SQLite path only: one open connection keeps the private in-memory database
    // alive for the whole factory lifetime while every request still uses its own
    // scoped DbContext, so reads genuinely round-trip through the database.
    private readonly SqliteConnection? _connection;

    // PostgreSQL path only: the connection string of this factory's throwaway
    // database, dropped on dispose. Assigned in ConfigureWebHost (not the
    // constructor) because the database to create is chosen by an overridable
    // member (CreatePostgresDatabase).
    private string? _postgresConnectionString;

    public WorkspaceApiFactory()
    {
        if (!_usePostgres)
        {
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (_usePostgres)
        {
            // Provision this factory's own throwaway database and point the
            // production persistence conditional (and so the production Npgsql
            // registration) at it.
            _postgresConnectionString = CreatePostgresDatabase();
            builder.UseSetting("ConnectionStrings:Database", _postgresConnectionString);
        }
        else
        {
            // Switch the production persistence conditional on (repositories, the
            // tenant context resolver, TimeProvider, the DbContext, the readiness
            // check are all registered exactly as in production).
            builder.UseSetting("ConnectionStrings:Database", _placeholderConnectionString);
        }

        builder.ConfigureServices(services =>
        {
            if (!_usePostgres)
            {
                // Replace the Npgsql DbContext options with SQLite over the shared
                // open connection. Production registers DbContextOptions via
                // AddDbContext<LiveCoreDbContext>(UseNpgsql(...)); remove every EF
                // options/configuration descriptor it added so only the SQLite
                // provider remains (otherwise EF reports two providers registered
                // in one service provider).
                RemoveDbContextRegistrations(services);

                // Give SQLite its own EF internal service provider so leftover
                // provider singletons can never collide with another provider in
                // the shared application container.
                var sqliteInternalServices = new ServiceCollection()
                    .AddEntityFrameworkSqlite()
                    .BuildServiceProvider();

                services.AddDbContext<LiveCoreDbContext>(options =>
                {
                    options
                        .UseSqlite(_connection!, ConfigureSqlite)
                        .UseInternalServiceProvider(sqliteInternalServices);

                    // A derived factory can observe the production query paths over SQLite (for example the
                    // CORE-PERF-003 authorization-cache tests count how many org/profile/membership SELECTs a
                    // request issues). The default adds nothing, so the swapped-in provider behaves exactly like
                    // production-on-SQLite.
                    var interceptors = CreateTestDbInterceptors();
                    if (interceptors.Length > 0)
                    {
                        options.AddInterceptors(interceptors);
                    }
                });
            }

            // Default the authentication AND authorization to the test scheme so
            // RequireAuthorization() on the workspace group challenges via the
            // test handler. This overrides the production default scheme for the
            // test host only.
            services.AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });

            services.AddAuthorizationBuilder()
                .SetDefaultPolicy(new AuthorizationPolicyBuilder(TestAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .Build());

            if (!_usePostgres)
            {
                // Initialize the swapped-in SQLite database (by default: create the
                // schema and enforce foreign keys, mirroring the repository tests'
                // SQLite setup). Under PostgreSQL the per-test database already
                // carries the migrated schema (or is intentionally left empty by the
                // startup-migration guard), so there is nothing to initialize.
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
                InitializeDatabase(context);
            }
        });
    }

    /// <summary>
    /// Provisions this factory's PostgreSQL database when the suite runs against
    /// PostgreSQL. The default returns a fresh database carrying the schema the real
    /// migrations produce (a copy of the migrated template). A derived factory can
    /// override this seam to provision an empty database instead (for example, the
    /// startup-migration guard asserts the host never creates the schema itself).
    /// </summary>
    protected virtual string CreatePostgresDatabase() => PostgresTestDatabase.CreateMigratedDatabase();

    /// <summary>
    /// Configures the SQLite provider options for the swapped-in test DbContext. The default does nothing, so
    /// the test host uses a plain non-retrying provider exactly like production-on-SQLite. A derived factory can
    /// override this seam to add, for example, a RETRYING execution strategy so the execution-strategy retry
    /// path (the production <c>EnableRetryOnFailure</c> posture) can be exercised over real HTTP (CORE-CONC-005).
    /// It is not called when the suite runs against PostgreSQL.
    /// </summary>
    protected virtual void ConfigureSqlite(SqliteDbContextOptionsBuilder sqlite)
    {
    }

    /// <summary>
    /// Supplies EF Core interceptors to attach to the swapped-in SQLite test DbContext. The default supplies
    /// none, so the test host behaves exactly like production-on-SQLite. A derived factory overrides this seam to
    /// observe the production query paths — for example, a command-counting interceptor that asserts the
    /// CORE-PERF-003 authorization cache stops a repeated request from re-issuing the org/profile/membership
    /// SELECTs. It is not called when the suite runs against PostgreSQL (the unchanged production registration is
    /// used there).
    /// </summary>
    protected virtual IInterceptor[] CreateTestDbInterceptors() => [];

    /// <summary>
    /// Prepares the swapped-in SQLite database for the test host. The default
    /// creates the schema with <c>EnsureCreated</c> and turns foreign keys on, so
    /// every test sees the full schema. A derived factory can override this seam
    /// (for example, to leave the schema absent and assert that the application's
    /// own startup never implicitly creates or migrates it). It is not called when
    /// the suite runs against PostgreSQL.
    /// </summary>
    protected virtual void InitializeDatabase(LiveCoreDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    /// <summary>
    /// Removes the production EF Core registrations for
    /// <see cref="LiveCoreDbContext"/> (the context, its options, the options
    /// configuration and the POOLING infrastructure the Npgsql
    /// <c>AddDbContextPool</c> added) so the SQLite provider can be registered
    /// cleanly without two providers colliding. Since CORE-PERF-003 the production
    /// host registers the context with <c>AddDbContextPool</c>, which additionally
    /// registers the pool and its scoped lease; those are stripped here too so the
    /// SQLite swap leaves only a single, non-pooled provider behind.
    /// </summary>
    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(LiveCoreDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType == typeof(DbContextOptions<LiveCoreDbContext>)
                || (descriptor.ServiceType.IsGenericType
                    && descriptor.ServiceType.GetGenericTypeDefinition().Name.StartsWith(
                        "IDbContextOptionsConfiguration", StringComparison.Ordinal))
                || descriptor.ServiceType.Name.Contains("DbContextPool", StringComparison.Ordinal)
                || descriptor.ServiceType.Name.Contains("ScopedDbContextLease", StringComparison.Ordinal))
            .ToArray();

        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    /// Runs a seeding action against a scoped <see cref="LiveCoreDbContext"/> with
    /// foreign keys enforced, then disposes the scope. Used to arrange
    /// organizations, memberships, workspaces and workspace memberships.
    /// </summary>
    public async Task SeedAsync(Func<LiveCoreDbContext, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        if (!_usePostgres)
        {
            // SQLite enforces foreign keys per-connection; PostgreSQL enforces them
            // by default and rejects this PRAGMA.
            context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        }

        await seed(context);
    }

    /// <summary>
    /// Creates an HttpClient acting as the given authenticated caller: the test
    /// auth handler reads these headers to build the principal's iss/sub and
    /// organization claims.
    /// </summary>
    public HttpClient CreateClientFor(string subject, string issuer, params string[] organizationClaims)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.SubjectHeader, subject);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IssuerHeader, issuer);
        if (organizationClaims.Length > 0)
        {
            client.DefaultRequestHeaders.Add(
                TestAuthenticationHandler.OrganizationHeader,
                string.Join(',', organizationClaims));
        }

        return client;
    }

    /// <summary>Creates an HttpClient with no token (no auth headers).</summary>
    public HttpClient CreateAnonymousClient() => CreateClient();

    // Guards the one-time cleanup so it runs exactly once and — for the per-test
    // PostgreSQL database — only AFTER the host has been fully torn down, never
    // while a live application connection is still holding the database open.
    private int _cleanedUp;
    private volatile bool _asyncDisposing;

    public override async ValueTask DisposeAsync()
    {
        _asyncDisposing = true;

        // Stop and dispose the host first (closing every application connection to
        // the per-test database), THEN drop it — otherwise DROP DATABASE would race
        // a live connection. base.DisposeAsync may invoke Dispose(true) internally,
        // which is why that path defers cleanup to here (see Dispose below).
        await base.DisposeAsync().ConfigureAwait(false);

        Cleanup();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // The async path drops after base.DisposeAsync completes; only the
        // synchronous path cleans up here (after the host has been disposed above).
        if (disposing && !_asyncDisposing)
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1)
        {
            return;
        }

        _connection?.Dispose();

        if (_postgresConnectionString is not null)
        {
            PostgresTestDatabase.DropDatabase(_postgresConnectionString);
        }
    }
}
