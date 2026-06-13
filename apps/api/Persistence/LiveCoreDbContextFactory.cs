using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Design-time factory for the EF Core tooling (<c>dotnet ef</c>).
///
/// It serves two jobs that the Core's migration application path
/// (CORE-OPS-001, <c>docs/13_SELF_HOSTING_REQUIREMENTS.md</c>) depends on:
///
/// <list type="number">
///   <item>Offline scaffolding (<c>dotnet ef migrations add</c>,
///   <c>dotnet ef migrations script</c>, building the migrations bundle) needs
///   only the model and the PostgreSQL provider; it never opens a connection, so
///   the credential-free <see cref="ScaffoldingPlaceholderConnectionString"/> is
///   sufficient and is the default.</item>
///   <item>Applying the migrations — <c>dotnet ef database update</c> and the
///   shipped migrations bundle (<c>apps/api/Migrations.Dockerfile</c>) both build
///   the context through this factory — needs the real target database. It is
///   supplied out of band through the <b>same</b> configuration key the API
///   runtime reads, <c>ConnectionStrings:Database</c> (environment variable
///   <see cref="ConnectionStringEnvironmentVariable"/>), so one mechanism applies
///   the schema to any environment. The <c>efbundle --connection</c> flag still
///   overrides it.</item>
/// </list>
///
/// No credentials live in this repository (threat T7): the only embedded value is
/// the credential-free local placeholder; every real connection string is read
/// from the environment. At runtime the context is registered only when
/// <c>ConnectionStrings:Database</c> is configured (see Program.cs), and the host
/// never migrates implicitly on startup — the schema is applied as a deployment
/// step so a multi-instance rollout never races to migrate.
/// </summary>
internal sealed class LiveCoreDbContextFactory : IDesignTimeDbContextFactory<LiveCoreDbContext>
{
    /// <summary>
    /// Environment variable that carries the target database connection string,
    /// matching the API runtime's configuration key <c>ConnectionStrings:Database</c>
    /// (the double-underscore form .NET maps to a configuration section).
    /// </summary>
    internal const string ConnectionStringEnvironmentVariable = "ConnectionStrings__Database";

    /// <summary>
    /// Credential-free connection string used for offline scaffolding, which never
    /// opens a connection. It carries no password so no secret lives in source.
    /// </summary>
    internal const string ScaffoldingPlaceholderConnectionString =
        "Host=localhost;Database=livecore-design-time";

    public LiveCoreDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseNpgsql(ResolveConnectionString())
            .Options;

        return new LiveCoreDbContext(options);
    }

    /// <summary>
    /// Resolves the connection string from the environment
    /// (<see cref="ConnectionStringEnvironmentVariable"/>), falling back to the
    /// credential-free <see cref="ScaffoldingPlaceholderConnectionString"/>.
    /// </summary>
    internal static string ResolveConnectionString()
        => ResolveConnectionString(
            Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable));

    /// <summary>
    /// Returns <paramref name="configuredConnectionString"/> when it is a non-blank
    /// value, otherwise the credential-free
    /// <see cref="ScaffoldingPlaceholderConnectionString"/>. A blank value is
    /// treated as unconfigured so an empty environment variable can never produce a
    /// malformed connection string.
    /// </summary>
    internal static string ResolveConnectionString(string? configuredConnectionString)
        => string.IsNullOrWhiteSpace(configuredConnectionString)
            ? ScaffoldingPlaceholderConnectionString
            : configuredConnectionString;
}
