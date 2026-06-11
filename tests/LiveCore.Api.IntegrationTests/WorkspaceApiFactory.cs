using LiveCore.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
///   the production wiring. The Npgsql provider is then replaced with EF Core
///   SQLite (a private in-memory database kept alive by one open connection,
///   foreign keys ON) so no PostgreSQL server is needed, mirroring the existing
///   repository tests' SQLite setup.</item>
///   <item>A test authentication scheme
///   (<see cref="TestAuthenticationHandler"/>) registered as the default scheme,
///   so <c>RequireAuthorization()</c> on the workspace route group authenticates
///   from request headers instead of a real identity provider. Production auth
///   wiring is untouched.</item>
/// </list>
///
/// The shared SQLite connection means every request in a test sees the same
/// seeded database. Use <see cref="SeedAsync"/> to arrange data and the
/// <c>Create*Client</c> helpers to act as a chosen caller.
/// </summary>
internal sealed class WorkspaceApiFactory : WebApplicationFactory<Program>
{
    // A placeholder connection string only switches the production persistence
    // conditional on; the actual provider is replaced with SQLite below. No real
    // PostgreSQL connection is ever opened.
    private const string _placeholderConnectionString =
        "Host=localhost;Port=5432;Database=livecore-integration-test";

    private readonly SqliteConnection _connection;

    public WorkspaceApiFactory()
    {
        // One open connection keeps the private in-memory database alive for the
        // whole factory lifetime while every request still uses its own scoped
        // DbContext, so reads genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Switch the production persistence conditional on (repositories, the
        // tenant context resolver, TimeProvider, the DbContext, the readiness
        // check are all registered exactly as in production).
        builder.UseSetting("ConnectionStrings:Database", _placeholderConnectionString);

        builder.ConfigureServices(services =>
        {
            // Replace the Npgsql DbContext options with SQLite over the shared
            // open connection. Production registers DbContextOptions via
            // AddDbContext<LiveCoreDbContext>(UseNpgsql(...)); remove every EF
            // options/configuration descriptor it added so only the SQLite
            // provider remains (otherwise EF reports two providers registered in
            // one service provider).
            RemoveDbContextRegistrations(services);

            // Give SQLite its own EF internal service provider so leftover
            // provider singletons can never collide with another provider in the
            // shared application container.
            var sqliteInternalServices = new ServiceCollection()
                .AddEntityFrameworkSqlite()
                .BuildServiceProvider();

            services.AddDbContext<LiveCoreDbContext>(options => options
                .UseSqlite(_connection)
                .UseInternalServiceProvider(sqliteInternalServices));

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

            // Create the schema and enforce foreign keys, mirroring the repository
            // tests' SQLite setup.
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
            context.Database.EnsureCreated();
            context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        });
    }

    /// <summary>
    /// Removes the production EF Core registrations for
    /// <see cref="LiveCoreDbContext"/> (the context, its options and the options
    /// configuration the Npgsql <c>AddDbContext</c> added) so the SQLite provider
    /// can be registered cleanly without two providers colliding.
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
                        "IDbContextOptionsConfiguration", StringComparison.Ordinal)))
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
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}
