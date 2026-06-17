// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetCleanupOptions"/> (CORE-AST-006) — the background cleanup job's deployment
/// policy (retention window, sweep interval, batch size). They assert the configuration is read with safe
/// defaults (so the worker runs without any cleanup configuration), explicit values are honored, every value
/// is validated as positive (a misconfiguration can never disable the grace window and reclaim in-flight
/// uploads), and a present-but-malformed value is rejected rather than silently ignored. Generic Asset
/// vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class AssetCleanupOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_values()
    {
        var options = new AssetCleanupOptions(TimeSpan.FromHours(12), TimeSpan.FromMinutes(30), 250);

        Assert.Equal(TimeSpan.FromHours(12), options.PendingRetention);
        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(250, options.BatchSize);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_pending_retention()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new AssetCleanupOptions(TimeSpan.Zero, TimeSpan.FromHours(1), 100));

    [Fact]
    public void Constructor_rejects_a_non_positive_sweep_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new AssetCleanupOptions(TimeSpan.FromHours(24), TimeSpan.Zero, 100));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_a_non_positive_batch_size(int batchSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new AssetCleanupOptions(TimeSpan.FromHours(24), TimeSpan.FromHours(1), batchSize));

    [Fact]
    public void FromConfiguration_falls_back_to_the_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = AssetCleanupOptions.FromConfiguration(configuration);

        Assert.Equal(AssetCleanupOptions.DefaultPendingRetention, options.PendingRetention);
        Assert.Equal(AssetCleanupOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(AssetCleanupOptions.DefaultBatchSize, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Cleanup:PendingRetention"] = "2.00:00:00",
                ["Assets:Cleanup:SweepInterval"] = "00:15:00",
                ["Assets:Cleanup:BatchSize"] = "500",
            })
            .Build();

        var options = AssetCleanupOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromDays(2), options.PendingRetention);
        Assert.Equal(TimeSpan.FromMinutes(15), options.SweepInterval);
        Assert.Equal(500, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timespan()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Cleanup:PendingRetention"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AssetCleanupOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_batch_size()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Cleanup:BatchSize"] = "lots",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AssetCleanupOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_value_that_is_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Cleanup:BatchSize"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => AssetCleanupOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => AssetCleanupOptions.FromConfiguration(null!));
}
