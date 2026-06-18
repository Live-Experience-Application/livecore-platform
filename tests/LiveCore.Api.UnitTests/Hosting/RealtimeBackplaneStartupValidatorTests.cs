// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Tests for the realtime backplane topology guard (CORE-RES-006, the "Multi-Instance Runtime Correctness"
/// epic) — the app-side defence in depth for the misconfiguration CORE-DEP-009 made the Helm chart refuse to
/// render. They pin the pure decision behind the startup guard: with no backplane configured, more than one
/// declared API instance is a definite misconfiguration in ANY environment (refuse to start, the fail-closed
/// default), a single instance in a container/production deployment is a prominent warning, and a single
/// instance in development is silent; a configured backplane is silent for every topology. The end-to-end
/// "the host refuses to start / starts" wiring over the real <c>Program</c> is covered by the smoke suite.
/// </summary>
public class RealtimeBackplaneStartupValidatorTests
{
    // ----- The pure topology decision (Evaluate) -----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void A_configured_backplane_starts_silently_for_any_instance_count_in_production(int instanceCount)
    {
        // A configured backplane fans realtime out across instances, so every topology is correct — the
        // "a correctly configured backplane starts silently" acceptance criterion.
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount, backplaneConfigured: true, isProductionEnvironment: true);

        Assert.Equal(RealtimeBackplaneStartupStatus.Ok, status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void A_configured_backplane_starts_silently_for_any_instance_count_outside_production(int instanceCount)
    {
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount, backplaneConfigured: true, isProductionEnvironment: false);

        Assert.Equal(RealtimeBackplaneStartupStatus.Ok, status);
    }

    [Fact]
    public void A_single_instance_development_run_without_a_backplane_is_unaffected()
    {
        // "single-instance development is unaffected": the default single-instance dev run stays silent so the
        // host (and the smoke tests) start without configuring a backplane.
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount: 1, backplaneConfigured: false, isProductionEnvironment: false);

        Assert.Equal(RealtimeBackplaneStartupStatus.Ok, status);
    }

    [Fact]
    public void A_single_instance_production_run_without_a_backplane_warns()
    {
        // "whenever the in-process backplane is selected in a container/production environment ... logs a single
        // prominent startup warning that cross-instance realtime delivery is disabled."
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount: 1, backplaneConfigured: false, isProductionEnvironment: true);

        Assert.Equal(RealtimeBackplaneStartupStatus.SingleInstanceWarning, status);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]
    public void Multiple_instances_without_a_backplane_refuse_to_start_in_production(int instanceCount)
    {
        // "When the API is configured to run as more than one instance ... and no realtime backplane connection
        // string is configured, the app fails fast at startup with a clear error."
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount, backplaneConfigured: false, isProductionEnvironment: true);

        Assert.Equal(RealtimeBackplaneStartupStatus.MultiInstanceMisconfigured, status);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(100)]
    public void Multiple_instances_without_a_backplane_refuse_to_start_even_outside_production(int instanceCount)
    {
        // Fail-closed default: more than one instance with no backplane is broken EVERYWHERE (an event computed
        // on one instance never reaches clients on the others), so it is flagged irrespective of environment —
        // the misconfiguration is never silently allowed just because the host is not in Production.
        var status = RealtimeBackplaneStartupValidator.Evaluate(
            instanceCount, backplaneConfigured: false, isProductionEnvironment: false);

        Assert.Equal(RealtimeBackplaneStartupStatus.MultiInstanceMisconfigured, status);
    }

    // ----- Reading the declared instance count -----

    [Fact]
    public void An_absent_instance_count_defaults_to_a_single_instance()
    {
        var configuration = BuildConfiguration(instanceCount: null);

        Assert.Equal(
            RealtimeBackplaneStartupValidator.DefaultInstanceCount,
            RealtimeBackplaneStartupValidator.ReadConfiguredInstanceCount(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2.5")]
    public void A_blank_unparseable_or_non_positive_instance_count_falls_back_to_a_single_instance(string raw)
    {
        // A malformed topology hint must never make the host worse than the documented single-instance default.
        var configuration = BuildConfiguration(raw);

        Assert.Equal(
            RealtimeBackplaneStartupValidator.DefaultInstanceCount,
            RealtimeBackplaneStartupValidator.ReadConfiguredInstanceCount(configuration));
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("  4  ", 4)] // surrounding whitespace is trimmed
    [InlineData("25", 25)]
    public void A_valid_instance_count_is_read_verbatim(string raw, int expected)
    {
        var configuration = BuildConfiguration(raw);

        Assert.Equal(expected, RealtimeBackplaneStartupValidator.ReadConfiguredInstanceCount(configuration));
    }

    // ----- The documented configuration contract -----

    [Fact]
    public void Configuration_keys_are_the_documented_contract()
    {
        // The published contract (.env.example, docs/11, docs/13) names these keys; pin them so the docs and code
        // stay in step, exactly as the production-config contract pins its keys.
        Assert.Equal("Realtime:InstanceCount", RealtimeBackplaneStartupValidator.InstanceCountKey);
        Assert.Equal(
            "Realtime:Backplane:ConnectionString", RealtimeBackplaneStartupValidator.BackplaneConnectionStringKey);
        Assert.Equal(1, RealtimeBackplaneStartupValidator.DefaultInstanceCount);
    }

    private static IConfiguration BuildConfiguration(string? instanceCount)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RealtimeBackplaneStartupValidator.InstanceCountKey] = instanceCount,
            })
            .Build();
}
