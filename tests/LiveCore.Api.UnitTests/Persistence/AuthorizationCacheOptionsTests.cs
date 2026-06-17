// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="AuthorizationCacheOptions"/> (CORE-PERF-003) — the per-request authorization-lookup
/// cache's deployment tuning. They assert the configuration is read with safe defaults (so a host runs without any
/// cache tuning), explicit values are honored, the TTL is validated positive (a misconfiguration can never make a
/// degenerate or unbounded cache), and a present-but-malformed value is rejected rather than silently ignored.
/// Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AuthorizationCacheOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_values()
    {
        var options = new AuthorizationCacheOptions(enabled: false, ttl: TimeSpan.FromSeconds(5));

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Ttl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_a_non_positive_ttl(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new AuthorizationCacheOptions(enabled: true, ttl: TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FromConfiguration_falls_back_to_the_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = AuthorizationCacheOptions.FromConfiguration(configuration);

        // ON by default with the short default TTL — the cache is correct-by-construction without any tuning.
        Assert.True(options.Enabled);
        Assert.Equal(AuthorizationCacheOptions.DefaultTtl, options.Ttl);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthorizationCache:Enabled"] = "false",
                ["AuthorizationCache:Ttl"] = "00:00:30",
            })
            .Build();

        var options = AuthorizationCacheOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Ttl);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_enabled_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AuthorizationCache:Enabled"] = "yes" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AuthorizationCacheOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_ttl_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AuthorizationCache:Ttl"] = "soon" })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AuthorizationCacheOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_non_positive_ttl_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AuthorizationCache:Ttl"] = "00:00:00" })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => AuthorizationCacheOptions.FromConfiguration(configuration));
    }
}
