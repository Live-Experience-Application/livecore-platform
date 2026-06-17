using LiveCore.Api.IdentityAccess;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="OidcBackchannelOptions"/> (CORE-RES-005) — the tunable bounds on the OIDC JWT-bearer
/// backchannel that make a slow or unreachable identity provider FAIL FAST instead of stalling token validation.
/// They assert the configuration is read with safe defaults (so a host runs without any OIDC tuning), explicit
/// values are honored, each interval is validated as strictly positive (a misconfiguration can never silently
/// remove the bound), and a present-but-malformed value is rejected at startup rather than silently ignored.
/// Generic, product-neutral vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class OidcBackchannelOptionsTests
{
    private static OidcBackchannelOptions FromValues(IDictionary<string, string?> values)
        => OidcBackchannelOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()
                .GetSection(OidcAuthenticationExtensions.ConfigurationSection));

    [Fact]
    public void Constructor_keeps_valid_intervals()
    {
        var options = new OidcBackchannelOptions(
            TimeSpan.FromSeconds(10), TimeSpan.FromHours(3), TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromSeconds(10), options.BackchannelTimeout);
        Assert.Equal(TimeSpan.FromHours(3), options.AutomaticRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.RefreshInterval);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_backchannel_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OidcBackchannelOptions(TimeSpan.Zero, TimeSpan.FromHours(6), TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OidcBackchannelOptions(TimeSpan.FromSeconds(-1), TimeSpan.FromHours(6), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_automatic_refresh_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new OidcBackchannelOptions(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(5)));

    [Fact]
    public void Constructor_rejects_a_non_positive_refresh_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new OidcBackchannelOptions(TimeSpan.FromSeconds(30), TimeSpan.FromHours(6), TimeSpan.Zero));

    [Fact]
    public void Defaults_are_positive_and_the_timeout_is_short()
    {
        Assert.True(OidcBackchannelOptions.DefaultBackchannelTimeout > TimeSpan.Zero);
        // Shorter than the framework's 60-second backchannel default, so a slow provider fails fast.
        Assert.True(OidcBackchannelOptions.DefaultBackchannelTimeout < TimeSpan.FromSeconds(60));
        Assert.True(OidcBackchannelOptions.DefaultAutomaticRefreshInterval > TimeSpan.Zero);
        Assert.True(OidcBackchannelOptions.DefaultRefreshInterval > TimeSpan.Zero);

        Assert.Equal(OidcBackchannelOptions.DefaultBackchannelTimeout, OidcBackchannelOptions.Default.BackchannelTimeout);
        Assert.Equal(
            OidcBackchannelOptions.DefaultAutomaticRefreshInterval, OidcBackchannelOptions.Default.AutomaticRefreshInterval);
        Assert.Equal(OidcBackchannelOptions.DefaultRefreshInterval, OidcBackchannelOptions.Default.RefreshInterval);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_defaults_when_unset()
    {
        var options = OidcBackchannelOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal(OidcBackchannelOptions.DefaultBackchannelTimeout, options.BackchannelTimeout);
        Assert.Equal(OidcBackchannelOptions.DefaultAutomaticRefreshInterval, options.AutomaticRefreshInterval);
        Assert.Equal(OidcBackchannelOptions.DefaultRefreshInterval, options.RefreshInterval);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_defaults_when_blank()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:BackchannelTimeout"] = "   ",
            ["Authentication:Oidc:AutomaticRefreshInterval"] = "",
            ["Authentication:Oidc:RefreshInterval"] = "  ",
        });

        Assert.Equal(OidcBackchannelOptions.DefaultBackchannelTimeout, options.BackchannelTimeout);
        Assert.Equal(OidcBackchannelOptions.DefaultAutomaticRefreshInterval, options.AutomaticRefreshInterval);
        Assert.Equal(OidcBackchannelOptions.DefaultRefreshInterval, options.RefreshInterval);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_intervals()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:BackchannelTimeout"] = "00:00:07",
            ["Authentication:Oidc:AutomaticRefreshInterval"] = "02:00:00",
            ["Authentication:Oidc:RefreshInterval"] = "00:03:00",
        });

        Assert.Equal(TimeSpan.FromSeconds(7), options.BackchannelTimeout);
        Assert.Equal(TimeSpan.FromHours(2), options.AutomaticRefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(3), options.RefreshInterval);
    }

    [Theory]
    [InlineData("Authentication:Oidc:BackchannelTimeout")]
    [InlineData("Authentication:Oidc:AutomaticRefreshInterval")]
    [InlineData("Authentication:Oidc:RefreshInterval")]
    public void FromConfiguration_rejects_a_malformed_interval(string key)
        => Assert.Throws<InvalidOperationException>(() => FromValues(new Dictionary<string, string?>
        {
            [key] = "not-a-timespan",
        }));

    [Fact]
    public void FromConfiguration_rejects_a_zero_backchannel_timeout_as_out_of_range()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FromValues(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:BackchannelTimeout"] = "00:00:00",
        }));

    [Fact]
    public void FromConfiguration_rejects_a_null_section()
        => Assert.Throws<ArgumentNullException>(() => OidcBackchannelOptions.FromConfiguration(null!));
}
