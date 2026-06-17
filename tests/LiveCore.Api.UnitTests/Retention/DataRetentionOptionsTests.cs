// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Retention;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Retention;

/// <summary>
/// Unit tests for <see cref="DataRetentionOptions"/> (CORE-PRIV-003/CORE-PRIV-006): the safe, disabled-by-default-
/// where-surprising configuration of the data-retention sweep. They assert the defaults (sessions/recaps/exports
/// disabled, invitations and idempotency keys enabled — the acceptance criteria), the configuration binding, and
/// the fail-closed validation that a misconfigured timing/flag is a startup error rather than a degenerate or
/// surprising sweep.
/// </summary>
public class DataRetentionOptionsTests
{
    private static DataRetentionOptions FromPairs(params (string Key, string Value)[] pairs)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
        return DataRetentionOptions.FromConfiguration(configuration);
    }

    [Fact]
    public void Defaults_disable_the_surprising_families_and_enable_the_invitation_email_purge()
    {
        var options = FromPairs();

        Assert.Equal(DataRetentionOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(DataRetentionOptions.DefaultBatchSize, options.BatchSize);

        // Surprising deletions are disabled by default.
        Assert.False(options.Sessions.Enabled);
        Assert.False(options.Recaps.Enabled);
        Assert.False(options.Exports.Enabled);
        // The clear privacy-hygiene purges are enabled by default: a terminal invitation's plaintext email, and
        // the idempotency-key store whose unbounded growth this purge bounds (CORE-PRIV-006).
        Assert.True(options.Invitations.Enabled);
        Assert.True(options.IdempotencyKeys.Enabled);

        Assert.Equal(DataRetentionOptions.DefaultSessionRetention, options.Sessions.RetentionWindow);
        Assert.Equal(DataRetentionOptions.DefaultRecapRetention, options.Recaps.RetentionWindow);
        Assert.Equal(DataRetentionOptions.DefaultExportRetention, options.Exports.RetentionWindow);
        Assert.Equal(DataRetentionOptions.DefaultInvitationRetention, options.Invitations.RetentionWindow);
        Assert.Equal(DataRetentionOptions.DefaultIdempotencyKeyRetention, options.IdempotencyKeys.RetentionWindow);

        Assert.True(options.AnyEnabled);
    }

    [Fact]
    public void Configuration_binds_every_family_flag_and_window()
    {
        var options = FromPairs(
            ("Retention:SweepInterval", "00:30:00"),
            ("Retention:BatchSize", "25"),
            ("Retention:Sessions:Enabled", "true"),
            ("Retention:Sessions:RetentionWindow", "90.00:00:00"),
            ("Retention:Invitations:Enabled", "false"),
            ("Retention:IdempotencyKeys:Enabled", "false"),
            ("Retention:IdempotencyKeys:RetentionWindow", "7.00:00:00"));

        Assert.Equal(TimeSpan.FromMinutes(30), options.SweepInterval);
        Assert.Equal(25, options.BatchSize);
        Assert.True(options.Sessions.Enabled);
        Assert.Equal(TimeSpan.FromDays(90), options.Sessions.RetentionWindow);
        // An explicit false overrides the enabled-by-default invitation purge.
        Assert.False(options.Invitations.Enabled);
        // The idempotency-key family binds its own flag and window (CORE-PRIV-006).
        Assert.False(options.IdempotencyKeys.Enabled);
        Assert.Equal(TimeSpan.FromDays(7), options.IdempotencyKeys.RetentionWindow);
    }

    [Fact]
    public void All_windows_can_be_disabled()
    {
        // Both enabled-by-default families must be turned off for the sweep to be a complete no-op.
        var options = FromPairs(
            ("Retention:Invitations:Enabled", "false"),
            ("Retention:IdempotencyKeys:Enabled", "false"));
        Assert.False(options.AnyEnabled);
    }

    [Theory]
    [InlineData("Retention:SweepInterval", "not-a-timespan")]
    [InlineData("Retention:BatchSize", "not-an-int")]
    [InlineData("Retention:Sessions:Enabled", "not-a-bool")]
    [InlineData("Retention:Sessions:RetentionWindow", "not-a-timespan")]
    public void A_present_but_unparseable_value_is_a_startup_error(string key, string value)
        => Assert.Throws<InvalidOperationException>(() => FromPairs((key, value)));

    [Fact]
    public void A_non_positive_window_is_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new RetentionWindowOptions(enabled: true, retentionWindow: TimeSpan.Zero));

    [Theory]
    [InlineData("Retention:SweepInterval", "00:00:00")]
    [InlineData("Retention:BatchSize", "0")]
    [InlineData("Retention:Exports:RetentionWindow", "-1.00:00:00")]
    public void A_degenerate_timing_or_batch_is_rejected(string key, string value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => FromPairs((key, value)));
}
