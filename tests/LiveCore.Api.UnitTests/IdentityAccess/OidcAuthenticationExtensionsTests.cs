using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="OidcAuthenticationExtensions.IsMissingProductionAudience"/> (CORE-OPS-004) —
/// the pure decision behind the startup guard that makes the OIDC Audience effectively mandatory in a
/// Production environment. A configured Authority accepts tokens, but audience validation is enabled only
/// when an Audience is configured, so a blank Audience silently disables audience scoping (any token the
/// issuer signs is accepted). These pin the matrix: in Production, Authority-set + Audience-blank is a
/// misconfiguration (true); a configured Audience, the unconfigured-Authority fail-closed path, and any
/// non-Production environment are all allowed (false). The end-to-end "the host refuses to start" wiring is
/// covered over the real <c>Program</c> by the smoke suite.
/// </summary>
public class OidcAuthenticationExtensionsTests
{
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";

    [Fact]
    public void IsMissingProductionAudience_is_true_for_authority_set_and_audience_blank_in_production()
    {
        Assert.True(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, null, isProductionEnvironment: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMissingProductionAudience_treats_a_null_or_whitespace_audience_as_blank_in_production(string? audience)
    {
        Assert.True(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, audience, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_when_a_valid_audience_is_configured_in_production()
    {
        // The acceptance criterion's "valid audience accepted": a configured Audience is not a
        // misconfiguration, so the host starts and audience scoping is enabled.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, _audience, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_when_no_authority_is_configured_in_production()
    {
        // No Authority => the fail-closed default handler path, where no token is ever accepted, so a
        // blank Audience cannot widen access. The unconfigured case keeps its existing behavior.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(null, null, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_for_authority_set_and_audience_blank_outside_production()
    {
        // Outside Production a blank Audience stays tolerated (the same local-development latitude
        // RequireHttpsMetadata=false allows), so a dev run against a local Keycloak still starts.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, null, isProductionEnvironment: false));
    }

    [Fact]
    public void AddOidcAuthentication_applies_the_configured_backchannel_bounds_to_jwt_bearer()
    {
        // The bearer pipeline must carry the configured backchannel timeout and tuned configuration-refresh
        // intervals (CORE-RES-005), so a slow or unreachable OIDC provider fails fast rather than stalling token
        // validation. Resolving the options runs the post-configure that builds the backchannel HttpClient and the
        // configuration manager from the Authority — no network call is made.
        var options = ResolveJwtBearerOptions(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = _authority,
            ["Authentication:Oidc:Audience"] = _audience,
            ["Authentication:Oidc:BackchannelTimeout"] = "00:00:07",
            ["Authentication:Oidc:AutomaticRefreshInterval"] = "02:00:00",
            ["Authentication:Oidc:RefreshInterval"] = "00:03:00",
        });

        Assert.Equal(TimeSpan.FromSeconds(7), options.BackchannelTimeout);
        Assert.Equal(TimeSpan.FromHours(2), options.AutomaticRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(3), options.RefreshInterval);
        // The backchannel HttpClient the handler uses to fetch discovery/JWKS carries the timeout, so an
        // unreachable provider's fetch is aborted at that bound (it never stalls).
        Assert.NotNull(options.Backchannel);
        Assert.Equal(TimeSpan.FromSeconds(7), options.Backchannel.Timeout);
    }

    [Fact]
    public void AddOidcAuthentication_applies_the_safe_default_backchannel_timeout_when_unset()
    {
        var options = ResolveJwtBearerOptions(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = _authority,
            ["Authentication:Oidc:Audience"] = _audience,
        });

        // A short, safe ceiling is applied even with no OIDC tuning configured.
        Assert.Equal(OidcBackchannelOptions.DefaultBackchannelTimeout, options.BackchannelTimeout);
        Assert.NotNull(options.Backchannel);
        Assert.Equal(OidcBackchannelOptions.DefaultBackchannelTimeout, options.Backchannel.Timeout);
    }

    private static JwtBearerOptions ResolveJwtBearerOptions(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        var configured = services.AddOidcAuthentication(
            configuration, new FakeHostEnvironment(Environments.Development));
        Assert.True(configured);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Minimal <see cref="IHostEnvironment"/> for the wiring tests.
    /// <see cref="OidcAuthenticationExtensions.AddOidcAuthentication"/> only reads the environment name (via
    /// <c>IsProduction()</c>); the rest is never touched here.
    /// </summary>
    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "LiveCore.Api.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
