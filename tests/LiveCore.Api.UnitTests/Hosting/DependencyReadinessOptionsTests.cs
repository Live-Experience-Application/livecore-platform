// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="DependencyReadinessOptions"/> (CORE-OBS-009; the timeout half pairs with
/// CORE-RES-005) — the short, configurable per-probe timeout that bounds the deep readiness reachability checks.
/// They assert the configuration is read with a safe short default (so a host runs without any readiness tuning),
/// an explicit value is honored, the timeout is validated as strictly positive (a misconfiguration can never
/// silently remove the bound), and a present-but-malformed value is rejected at startup rather than silently
/// ignored. Generic, product-neutral vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class DependencyReadinessOptionsTests
{
    private static DependencyReadinessOptions FromValues(IDictionary<string, string?> values)
        => DependencyReadinessOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Constructor_keeps_a_valid_timeout()
        => Assert.Equal(TimeSpan.FromSeconds(5), new DependencyReadinessOptions(TimeSpan.FromSeconds(5)).ProbeTimeout);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_a_non_positive_timeout(int seconds)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new DependencyReadinessOptions(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Default_is_positive_and_short()
    {
        Assert.True(DependencyReadinessOptions.DefaultProbeTimeout > TimeSpan.Zero);
        // Short by design: readiness is polled frequently, so an unreachable dependency must fail the probe fast.
        Assert.True(DependencyReadinessOptions.DefaultProbeTimeout <= TimeSpan.FromSeconds(5));
        Assert.Equal(DependencyReadinessOptions.DefaultProbeTimeout, DependencyReadinessOptions.Default.ProbeTimeout);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_default_when_unset()
        => Assert.Equal(
            DependencyReadinessOptions.DefaultProbeTimeout,
            DependencyReadinessOptions.FromConfiguration(new ConfigurationBuilder().Build()).ProbeTimeout);

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void FromConfiguration_falls_back_to_the_default_when_blank(string value)
        => Assert.Equal(
            DependencyReadinessOptions.DefaultProbeTimeout,
            FromValues(new Dictionary<string, string?>
            {
                ["HealthChecks:Readiness:ProbeTimeout"] = value,
            }).ProbeTimeout);

    [Fact]
    public void FromConfiguration_reads_the_configured_timeout()
        => Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            FromValues(new Dictionary<string, string?>
            {
                ["HealthChecks:Readiness:ProbeTimeout"] = "00:00:00.750",
            }).ProbeTimeout);

    [Fact]
    public void FromConfiguration_rejects_a_malformed_timeout()
        => Assert.Throws<InvalidOperationException>(() => FromValues(new Dictionary<string, string?>
        {
            ["HealthChecks:Readiness:ProbeTimeout"] = "not-a-timespan",
        }));

    [Fact]
    public void FromConfiguration_rejects_a_zero_timeout_as_out_of_range()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FromValues(new Dictionary<string, string?>
        {
            ["HealthChecks:Readiness:ProbeTimeout"] = "00:00:00",
        }));

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => DependencyReadinessOptions.FromConfiguration(null!));
}
