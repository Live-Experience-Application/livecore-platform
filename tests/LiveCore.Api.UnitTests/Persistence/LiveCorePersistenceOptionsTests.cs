// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for <see cref="LiveCorePersistenceOptions"/> (CORE-RES-004) — the tunable per-command timeouts that
/// give a stuck query a CEILING so retry cannot amplify it unbounded. They assert the configuration is read with
/// safe defaults (so a host runs without any persistence tuning), explicit values are honored, the command timeout
/// is validated as positive (a misconfiguration can never silently remove the bound by setting it to "no timeout"),
/// the statement timeout may be disabled with zero but never negative, and a present-but-malformed value is rejected
/// at startup rather than silently ignored. Generic, product-neutral vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class LiveCorePersistenceOptionsTests
{
    [Fact]
    public void Constructor_keeps_valid_timeouts()
    {
        var options = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(10), options.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), options.StatementTimeout);
    }

    [Fact]
    public void Constructor_allows_a_zero_statement_timeout_to_disable_the_server_ceiling()
    {
        var options = new LiveCorePersistenceOptions(TimeSpan.FromSeconds(10), TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, options.StatementTimeout);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_command_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LiveCorePersistenceOptions(TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LiveCorePersistenceOptions(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Constructor_rejects_a_negative_statement_timeout()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new LiveCorePersistenceOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(-1)));

    [Fact]
    public void Defaults_are_positive_and_bounded()
    {
        Assert.True(LiveCorePersistenceOptions.DefaultCommandTimeout > TimeSpan.Zero);
        Assert.True(LiveCorePersistenceOptions.DefaultStatementTimeout > TimeSpan.Zero);
        Assert.Equal(LiveCorePersistenceOptions.DefaultCommandTimeout, LiveCorePersistenceOptions.Default.CommandTimeout);
        Assert.Equal(LiveCorePersistenceOptions.DefaultStatementTimeout, LiveCorePersistenceOptions.Default.StatementTimeout);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = LiveCorePersistenceOptions.FromConfiguration(configuration);

        Assert.Equal(LiveCorePersistenceOptions.DefaultCommandTimeout, options.CommandTimeout);
        Assert.Equal(LiveCorePersistenceOptions.DefaultStatementTimeout, options.StatementTimeout);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_defaults_when_blank()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:CommandTimeout"] = "   ",
                ["Persistence:StatementTimeout"] = "",
            })
            .Build();

        var options = LiveCorePersistenceOptions.FromConfiguration(configuration);

        Assert.Equal(LiveCorePersistenceOptions.DefaultCommandTimeout, options.CommandTimeout);
        Assert.Equal(LiveCorePersistenceOptions.DefaultStatementTimeout, options.StatementTimeout);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_timeouts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:CommandTimeout"] = "00:00:20",
                ["Persistence:StatementTimeout"] = "00:00:25",
            })
            .Build();

        var options = LiveCorePersistenceOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromSeconds(20), options.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), options.StatementTimeout);
    }

    [Fact]
    public void FromConfiguration_reads_a_zero_statement_timeout_as_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:StatementTimeout"] = "00:00:00",
            })
            .Build();

        var options = LiveCorePersistenceOptions.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.Zero, options.StatementTimeout);
    }

    [Fact]
    public void FromConfiguration_rejects_a_malformed_command_timeout()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:CommandTimeout"] = "not-a-timespan",
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => LiveCorePersistenceOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_zero_command_timeout_as_out_of_range()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:CommandTimeout"] = "00:00:00",
            })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => LiveCorePersistenceOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => LiveCorePersistenceOptions.FromConfiguration(null!));
}
