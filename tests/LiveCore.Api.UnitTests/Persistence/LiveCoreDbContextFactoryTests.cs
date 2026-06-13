using LiveCore.Api.Persistence;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Connection-resolution tests for the design-time factory that backs the
/// database migration application path (CORE-OPS-001;
/// docs/13_SELF_HOSTING_REQUIREMENTS.md). Applying the migrations — a local
/// <c>dotnet ef database update</c> and the shipped migrations bundle
/// (apps/api/Migrations.Dockerfile) — builds the context through this factory, so
/// the connection string it resolves is the seam the schema is applied through.
/// </summary>
public class LiveCoreDbContextFactoryTests
{
    [Fact]
    public void Uses_the_configured_connection_string_so_the_runner_targets_the_real_database()
    {
        const string configured =
            "Host=db.internal;Port=5432;Database=livecore;Username=livecore;Password=secret";

        var resolved = LiveCoreDbContextFactory.ResolveConnectionString(configured);

        Assert.Equal(configured, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Falls_back_to_the_credential_free_placeholder_when_unconfigured(string? configured)
    {
        // An absent or blank value is treated as unconfigured (offline scaffolding,
        // which never opens a connection) rather than a malformed connection string.
        var resolved = LiveCoreDbContextFactory.ResolveConnectionString(configured);

        Assert.Equal(LiveCoreDbContextFactory.ScaffoldingPlaceholderConnectionString, resolved);
    }

    [Fact]
    public void Placeholder_carries_no_credentials_so_no_secret_lives_in_source()
    {
        // Threat T7: the only embedded connection string is the local placeholder,
        // and it must never carry a password — every real connection string is
        // supplied from the environment.
        var placeholder = LiveCoreDbContextFactory.ScaffoldingPlaceholderConnectionString;

        Assert.DoesNotContain("Password", placeholder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pwd", placeholder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Connection_string_environment_variable_matches_the_api_runtime_configuration_key()
    {
        // The runner reads the same key the API runtime reads
        // (ConnectionStrings:Database), whose environment-variable form uses the
        // double-underscore section separator .NET maps to configuration.
        Assert.Equal(
            "ConnectionStrings__Database",
            LiveCoreDbContextFactory.ConnectionStringEnvironmentVariable);
    }
}
