// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Tests for the production required-dependency readiness gate (CORE-OPS-005). The API previously reported
/// READY whenever no readiness check failed, and the database check is only registered when a connection
/// string is configured — so a host with no persistence (or no OIDC identity provider) advertised a readiness
/// it did not have. These tests pin the pure decision and the health-check behavior: in a Production
/// environment an unconfigured required dependency (persistence or OIDC) is NOT-READY, while outside
/// Production the check is inert (local-development latitude), the same environment-aware posture as the OIDC
/// audience guard (CORE-OPS-004).
/// </summary>
public class RequiredDependencyReadinessTests
{
    [Theory]
    // In Production a missing required dependency (persistence or OIDC) is not-ready.
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)] // persistence missing
    [InlineData(true, true, false, true)] // OIDC missing
    [InlineData(true, true, true, false)] // both configured -> ready
    // Outside Production the gate is inert, even fully unconfigured (the latitude the host already grants).
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    public void RequiredDependencyMissing_is_true_only_in_production_with_a_missing_dependency(
        bool isProduction,
        bool persistenceConfigured,
        bool oidcConfigured,
        bool expected)
    {
        Assert.Equal(
            expected,
            RequiredDependencyReadiness.RequiredDependencyMissing(
                isProduction, persistenceConfigured, oidcConfigured));
    }

    [Theory]
    [InlineData(false, false)] // persistence missing
    [InlineData(true, false)] // OIDC missing
    [InlineData(false, true)] // persistence missing
    public async Task Health_check_reports_unhealthy_in_production_when_a_dependency_is_unconfigured(
        bool persistenceConfigured,
        bool oidcConfigured)
    {
        var check = new RequiredDependencyHealthCheck(
            isProductionEnvironment: true,
            persistenceConfigured: persistenceConfigured,
            oidcConfigured: oidcConfigured);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_check_reports_healthy_in_production_when_both_dependencies_are_configured()
    {
        var check = new RequiredDependencyHealthCheck(
            isProductionEnvironment: true,
            persistenceConfigured: true,
            oidcConfigured: true);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Health_check_is_inert_outside_production_even_fully_unconfigured()
    {
        var check = new RequiredDependencyHealthCheck(
            isProductionEnvironment: false,
            persistenceConfigured: false,
            oidcConfigured: false);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
