// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="GracefulShutdownOptions"/> (CORE-DEP-002) — the tuned, configurable drain window
/// both hosts apply to <c>HostOptions.ShutdownTimeout</c> so a rolling restart does not abruptly cut in-flight
/// work. They assert the configuration is read with a safe default (so both hosts run without any shutdown
/// configuration), an explicit value is honored, the window is validated as positive (a misconfiguration can
/// never collapse the drain window to nothing), and a present-but-malformed value is rejected rather than
/// silently ignored. Generic, product-neutral hosting vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class GracefulShutdownOptionsTests
{
    [Fact]
    public void Constructor_keeps_a_valid_window()
    {
        var options = new GracefulShutdownOptions(TimeSpan.FromSeconds(40));

        Assert.Equal(TimeSpan.FromSeconds(40), options.ShutdownTimeout);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_window()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GracefulShutdownOptions(TimeSpan.Zero));

    [Fact]
    public void Constructor_rejects_a_negative_window()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new GracefulShutdownOptions(TimeSpan.FromSeconds(-1)));

    [Fact]
    public void DefaultShutdownTimeout_is_below_the_conventional_30s_grace_period()
    {
        // The default is deliberately under the conventional 30s orchestration grace period so the process
        // exits cleanly before it is force-killed (docs/13_SELF_HOSTING_REQUIREMENTS.md). Pin that posture.
        Assert.True(GracefulShutdownOptions.DefaultShutdownTimeout > TimeSpan.Zero);
        Assert.True(GracefulShutdownOptions.DefaultShutdownTimeout < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_default_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = GracefulShutdownOptions.FromConfiguration(configuration);

        Assert.Equal(GracefulShutdownOptions.DefaultShutdownTimeout, options.ShutdownTimeout);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_default_when_blank()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:ShutdownTimeout"] = "   ",
            })
            .Build();

        var options = GracefulShutdownOptions.FromConfiguration(configuration);

        Assert.Equal(GracefulShutdownOptions.DefaultShutdownTimeout, options.ShutdownTimeout);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_window()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:ShutdownTimeout"] = "00:00:45",
            })
            .Build();

        var options = GracefulShutdownOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(45), options.ShutdownTimeout);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timespan()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:ShutdownTimeout"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => GracefulShutdownOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_window_that_is_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:ShutdownTimeout"] = "00:00:00",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => GracefulShutdownOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => GracefulShutdownOptions.FromConfiguration(null!));
}
