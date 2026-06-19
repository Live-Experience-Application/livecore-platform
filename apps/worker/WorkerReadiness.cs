// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Worker;

/// <summary>
/// The worker's production readiness gate (CORE-OBS-013, the "Worker and Health Observability Completeness"
/// epic) — the worker counterpart of the API's
/// <see cref="LiveCore.Api.Hosting.RequiredDependencyReadiness"/> (CORE-OPS-005).
///
/// <para>
/// Before this story the worker advertised only PER-LOOP LIVENESS (<see cref="WorkerJobHeartbeats"/>, served on
/// <c>/health/live</c>), which is VACUOUSLY healthy when there are no active loops — so a worker deployed with no
/// database (and therefore no scheduled loop) reported live while able to do no work at all. Liveness is the
/// wrong signal to fix that with: a process with nothing to do is genuinely alive, and restarting it would not
/// help, so per-loop liveness must STAY vacuously healthy (there is nothing to stall). The missing signal is
/// READINESS — "can this worker actually do its job?" — which is DISTINCT from liveness exactly as the API
/// separates <c>/health/ready</c> from <c>/health/live</c>.
/// </para>
///
/// In a PRODUCTION environment this gate reports NOT-READY (Unhealthy) when the worker has no required dependency
/// to work against — persistence (the database) is unconfigured — OR has ZERO active loops, so a worker that
/// cannot do any work is taken out of the ready rotation instead of advertising a readiness it does not have.
/// Outside Production the gate is inert (Healthy), preserving the local-development latitude the host already
/// grants (running with no database and no loop) — the same environment-aware, fail-closed posture as the API
/// readiness gate (CORE-OPS-005) and the OIDC audience guard (CORE-OPS-004). Unlike the API the worker requires
/// NO OIDC identity provider (it serves no authenticated request), so OIDC is not part of its required set.
///
/// <para>
/// The configured/unconfigured state and the active-loop count are both fixed at host build time, so the verdict
/// is captured here once rather than re-evaluated on every probe (exactly like the API gate captures its
/// startup-time booleans). The readiness response stays status-only (<see cref="WorkerHealthEndpoints"/>), so WHY
/// the worker is not ready is never leaked to the unauthenticated readiness endpoint — it is a startup log line,
/// not an HTTP detail (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
public static class WorkerReadiness
{
    /// <summary>
    /// Name the readiness check is registered under. Kept server-side (the readiness endpoint writes only the
    /// overall status), never leaked to the unauthenticated response (threat T7).
    /// </summary>
    public const string CheckName = "worker-required-dependencies";

    /// <summary>
    /// The single, pure decision the readiness gate enforces: in a Production environment the worker is NOT
    /// ready when persistence is unconfigured OR there are zero active job loops (the worker can do no useful
    /// work). Pure (no services, no configuration side effects) so the behavior is unit-testable directly,
    /// mirroring <see cref="LiveCore.Api.Hosting.RequiredDependencyReadiness.RequiredDependencyMissing"/>
    /// (CORE-OPS-005).
    /// </summary>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <param name="persistenceConfigured">Whether a database connection string is configured.</param>
    /// <param name="activeLoopCount">The number of job loops the host actually scheduled.</param>
    /// <returns>
    /// <see langword="true"/> only when, in Production, persistence is unconfigured or no loop is active.
    /// Returns <see langword="false"/> outside Production (local-development latitude) and when persistence is
    /// configured and at least one loop is active.
    /// </returns>
    public static bool WorkerNotReady(
        bool isProductionEnvironment,
        bool persistenceConfigured,
        int activeLoopCount)
        => isProductionEnvironment && (!persistenceConfigured || activeLoopCount <= 0);

    /// <summary>
    /// Registers the worker production readiness check (tagged <see cref="WorkerHealthEndpoints.ReadinessTag"/>)
    /// so <c>/health/ready</c> reports NOT-READY in Production when persistence is unconfigured or no loop is
    /// active. The configured state and the active-loop count are known at startup, so they are captured here
    /// rather than re-evaluated per probe. Registered UNCONDITIONALLY (independent of whether persistence is
    /// configured), precisely so it still runs in the no-database case — the case that would otherwise read as
    /// vacuously healthy.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="environment">The host environment (the Production check is environment-aware).</param>
    /// <param name="persistenceConfigured">Whether a database connection string is configured.</param>
    /// <param name="activeLoopCount">The number of job loops the host actually scheduled.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddWorkerReadinessCheck(
        this IServiceCollection services,
        IHostEnvironment environment,
        bool persistenceConfigured,
        int activeLoopCount)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddHealthChecks().AddCheck(
            CheckName,
            new WorkerReadinessHealthCheck(environment.IsProduction(), persistenceConfigured, activeLoopCount),
            tags: [WorkerHealthEndpoints.ReadinessTag]);

        return services;
    }
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> behind <see cref="WorkerReadiness.AddWorkerReadinessCheck"/>
/// (CORE-OBS-013). It captures the environment, the startup-time persistence-configured boolean and the active
/// loop count, and applies the pure <see cref="WorkerReadiness.WorkerNotReady"/> decision. The failure
/// description stays server-side (the readiness endpoint writes only the overall status), so it never tells an
/// anonymous caller WHY the worker is not ready (threat T7).
/// </summary>
internal sealed class WorkerReadinessHealthCheck : IHealthCheck
{
    private readonly bool _isProductionEnvironment;
    private readonly bool _persistenceConfigured;
    private readonly int _activeLoopCount;

    public WorkerReadinessHealthCheck(
        bool isProductionEnvironment,
        bool persistenceConfigured,
        int activeLoopCount)
    {
        _isProductionEnvironment = isProductionEnvironment;
        _persistenceConfigured = persistenceConfigured;
        _activeLoopCount = activeLoopCount;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (WorkerReadiness.WorkerNotReady(_isProductionEnvironment, _persistenceConfigured, _activeLoopCount))
        {
            // Generic, reason-agnostic description: it is inspected server-side and is never written to the
            // unauthenticated readiness response.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The worker is not ready: persistence is unconfigured or no job loop is active."));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
