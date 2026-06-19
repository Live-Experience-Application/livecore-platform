// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Worker;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.SmokeTests;

/// <summary>
/// Tests for the worker production readiness gate (CORE-OBS-013). Before this story the worker advertised only
/// per-loop LIVENESS, which is vacuously healthy with zero active loops — so a worker deployed to Production with
/// no database reported "live" while able to do no work at all. These pin the pure decision and the health-check
/// behavior behind the DISTINCT readiness signal: in a Production environment a worker with no required
/// dependency (persistence) OR zero active loops is NOT-READY (not vacuously healthy), while outside Production
/// the gate is inert (local-development latitude), the same environment-aware posture as the API readiness gate
/// (CORE-OPS-005) and the OIDC audience guard (CORE-OPS-004). The worker requires no OIDC, so OIDC is not part of
/// its required set.
/// </summary>
public class WorkerReadinessTests
{
    [Theory]
    // In Production a worker with no required dependency (persistence) or no active loop is NOT-READY.
    [InlineData(true, false, 0, true)] // persistence missing AND zero loops
    [InlineData(true, false, 3, true)] // persistence missing (even if loops were somehow counted)
    [InlineData(true, true, 0, true)] // zero active loops even though persistence is configured (not vacuous)
    [InlineData(true, true, 1, false)] // persistence configured and at least one loop -> ready
    [InlineData(true, true, 5, false)] // a fully-loaded worker -> ready
    // Outside Production the gate is inert, even fully unconfigured (the latitude the host already grants).
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 5, false)]
    public void WorkerNotReady_is_true_only_in_production_when_the_worker_can_do_no_work(
        bool isProduction,
        bool persistenceConfigured,
        int activeLoopCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkerReadiness.WorkerNotReady(isProduction, persistenceConfigured, activeLoopCount));
    }

    [Theory]
    [InlineData(false, 0)] // persistence missing and zero loops
    [InlineData(false, 4)] // persistence missing
    [InlineData(true, 0)] // zero active loops (the previously-vacuous case)
    public async Task Health_check_reports_unhealthy_in_production_when_the_worker_can_do_no_work(
        bool persistenceConfigured,
        int activeLoopCount)
    {
        var check = new WorkerReadinessHealthCheck(
            isProductionEnvironment: true,
            persistenceConfigured: persistenceConfigured,
            activeLoopCount: activeLoopCount);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Health_check_reports_healthy_in_production_when_persistence_and_a_loop_are_present()
    {
        var check = new WorkerReadinessHealthCheck(
            isProductionEnvironment: true,
            persistenceConfigured: true,
            activeLoopCount: 4);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Health_check_is_inert_outside_production_even_with_zero_loops_and_no_persistence()
    {
        var check = new WorkerReadinessHealthCheck(
            isProductionEnvironment: false,
            persistenceConfigured: false,
            activeLoopCount: 0);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
