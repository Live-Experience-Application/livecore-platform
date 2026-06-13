using LiveCore.Api.Realtime;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeBackplaneOptions"/> (CORE-OPS-007), the realtime backplane connection
/// settings read from the deployment's <c>Realtime:Backplane:*</c> configuration. They pin that the connection
/// string drives <see cref="RealtimeBackplaneOptions.IsConfigured"/> (so the registration selects the Redis
/// backplane only when a real connection string is present), that values are trimmed, and that a missing or
/// blank connection string yields an unconfigured value (the documented single-instance fallback). The
/// settings are read from configuration only — never hardcoded (threat T7). Generic vocabulary only (AGENTS.md).
/// </summary>
public class RealtimeBackplaneOptionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_is_unconfigured_when_no_connection_string_is_set()
    {
        var options = RealtimeBackplaneOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.IsConfigured);
        Assert.Null(options.ConnectionString);
        Assert.Null(options.ChannelPrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_is_unconfigured_for_a_blank_connection_string(string connectionString)
    {
        var options = RealtimeBackplaneOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = connectionString,
        }));

        Assert.False(options.IsConfigured);
        Assert.Null(options.ConnectionString);
    }

    [Fact]
    public void FromConfiguration_reads_and_trims_the_connection_string_and_channel_prefix()
    {
        var options = RealtimeBackplaneOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = "  valkey:6379  ",
            ["Realtime:Backplane:ChannelPrefix"] = "  livecore  ",
        }));

        Assert.True(options.IsConfigured);
        Assert.Equal("valkey:6379", options.ConnectionString);
        Assert.Equal("livecore", options.ChannelPrefix);
    }

    [Fact]
    public void FromConfiguration_rejects_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => RealtimeBackplaneOptions.FromConfiguration(null!));
}
