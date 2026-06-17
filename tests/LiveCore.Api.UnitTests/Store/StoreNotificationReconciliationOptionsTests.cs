// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for <see cref="StoreNotificationReconciliationOptions"/> (CORE-JOB-003) — the reconciliation job's
/// deployment policy (enablement gate, sweep cadence, batch size). They pin the FAIL-CLOSED enablement default
/// ("only runs when billing is configured": the job is OFF unless a deployment explicitly enables it), that the
/// configuration is otherwise read with safe defaults, that explicit values are honored, that every timing value
/// is validated as positive, and that a present-but-malformed value is rejected rather than silently ignored.
/// Generic vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class StoreNotificationReconciliationOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_values()
    {
        var options = new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.FromMinutes(30), 250);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(250, options.BatchSize);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_sweep_interval()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.Zero, 50));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_a_non_positive_batch_size(int batchSize)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.FromHours(1), batchSize));

    [Fact]
    public void FromConfiguration_is_disabled_by_default_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = StoreNotificationReconciliationOptions.FromConfiguration(configuration);

        // Fail-closed: billing is out of scope for v1, so the job does not run unless explicitly enabled.
        Assert.False(options.Enabled);
        Assert.Equal(StoreNotificationReconciliationOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(StoreNotificationReconciliationOptions.DefaultBatchSize, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:Reconciliation:Enabled"] = "true",
                ["Store:Reconciliation:SweepInterval"] = "00:15:00",
                ["Store:Reconciliation:BatchSize"] = "500",
            })
            .Build();

        var options = StoreNotificationReconciliationOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), options.SweepInterval);
        Assert.Equal(500, options.BatchSize);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_enabled_flag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:Reconciliation:Enabled"] = "maybe",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => StoreNotificationReconciliationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timespan()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:Reconciliation:SweepInterval"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => StoreNotificationReconciliationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_batch_size()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:Reconciliation:BatchSize"] = "lots",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => StoreNotificationReconciliationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_value_that_is_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Store:Reconciliation:BatchSize"] = "0",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => StoreNotificationReconciliationOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(
            () => StoreNotificationReconciliationOptions.FromConfiguration(null!));
}
