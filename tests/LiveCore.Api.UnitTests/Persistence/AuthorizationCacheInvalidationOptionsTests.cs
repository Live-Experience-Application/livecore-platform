// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="AuthorizationCacheInvalidationOptions"/> (CORE-RES-007) — the cross-instance
/// authorization-cache invalidation configuration. They assert it reuses the realtime backplane connection (there is
/// no second backplane to configure), is ON by default but only ACTIVE when a backplane connection string is present
/// (so a single-instance deployment stays on the no-op + TTL backstop), namespaces its channel by the backplane's
/// channel prefix, can be disabled by the opt-out toggle, and rejects a present-but-malformed toggle value rather
/// than silently ignoring it. Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AuthorizationCacheInvalidationOptionsTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_to_enabled_but_inactive_without_a_backplane()
    {
        var options = AuthorizationCacheInvalidationOptions.FromConfiguration(new ConfigurationBuilder().Build());

        // ON by default, but with no backplane configured it is INACTIVE: single-instance behaviour (no-op + TTL).
        Assert.True(options.Enabled);
        Assert.Null(options.ConnectionString);
        Assert.False(options.IsActive);
        Assert.Equal(AuthorizationCacheInvalidationOptions.ChannelSuffix, options.Channel);
    }

    [Fact]
    public void Is_active_when_a_backplane_connection_string_is_configured()
    {
        var options = AuthorizationCacheInvalidationOptions.FromConfiguration(Configuration(new()
        {
            ["Realtime:Backplane:ConnectionString"] = "valkey:6379",
        }));

        Assert.True(options.Enabled);
        Assert.Equal("valkey:6379", options.ConnectionString);
        Assert.True(options.IsActive);
        // No channel prefix configured: the channel is the bare suffix.
        Assert.Equal(AuthorizationCacheInvalidationOptions.ChannelSuffix, options.Channel);
    }

    [Fact]
    public void Namespaces_the_channel_by_the_backplane_channel_prefix()
    {
        var options = AuthorizationCacheInvalidationOptions.FromConfiguration(Configuration(new()
        {
            ["Realtime:Backplane:ConnectionString"] = "valkey:6379",
            ["Realtime:Backplane:ChannelPrefix"] = "tenant-x-",
        }));

        Assert.Equal("tenant-x-" + AuthorizationCacheInvalidationOptions.ChannelSuffix, options.Channel);
        Assert.True(options.IsActive);
    }

    [Fact]
    public void Is_inactive_when_the_toggle_is_off_even_with_a_backplane()
    {
        var options = AuthorizationCacheInvalidationOptions.FromConfiguration(Configuration(new()
        {
            ["Realtime:Backplane:ConnectionString"] = "valkey:6379",
            ["AuthorizationCache:CrossInstanceInvalidation"] = "false",
        }));

        // The opt-out toggle reverts to the TTL-only window even when a backplane is configured.
        Assert.False(options.Enabled);
        Assert.False(options.IsActive);
    }

    [Fact]
    public void Rejects_a_malformed_toggle_value()
    {
        var configuration = Configuration(new() { ["AuthorizationCache:CrossInstanceInvalidation"] = "maybe" });

        Assert.Throws<InvalidOperationException>(
            () => AuthorizationCacheInvalidationOptions.FromConfiguration(configuration));
    }
}
