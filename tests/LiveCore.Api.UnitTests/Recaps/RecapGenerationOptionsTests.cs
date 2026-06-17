// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Recaps;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Recaps;

/// <summary>
/// Unit tests for <see cref="RecapGenerationOptions"/> (CORE-JOB-001) — the background recap generation job's
/// deployment policy (sweep cadence, batch size). They assert the configuration is read with safe defaults (so
/// the worker runs without any recap configuration), explicit values are honored, every value is validated as
/// positive (a misconfiguration can never schedule a degenerate sweep), and a present-but-malformed value is
/// rejected rather than silently ignored. Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class RecapGenerationOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_values()
    {
        var options = new RecapGenerationOptions(TimeSpan.FromMinutes(30), 250);

        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(250, options.BatchSize);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_sweep_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecapGenerationOptions(TimeSpan.Zero, 50));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_a_non_positive_batch_size(int batchSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new RecapGenerationOptions(TimeSpan.FromHours(1), batchSize));

    [Fact]
    public void FromConfiguration_falls_back_to_the_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = RecapGenerationOptions.FromConfiguration(configuration);

        Assert.Equal(RecapGenerationOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(RecapGenerationOptions.DefaultBatchSize, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recaps:Generation:SweepInterval"] = "00:15:00",
                ["Recaps:Generation:BatchSize"] = "500",
            })
            .Build();

        var options = RecapGenerationOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromMinutes(15), options.SweepInterval);
        Assert.Equal(500, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timespan()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recaps:Generation:SweepInterval"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => RecapGenerationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_batch_size()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recaps:Generation:BatchSize"] = "lots",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => RecapGenerationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_value_that_is_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recaps:Generation:BatchSize"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => RecapGenerationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => RecapGenerationOptions.FromConfiguration(null!));
}
