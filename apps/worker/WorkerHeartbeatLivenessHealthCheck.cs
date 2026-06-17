// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Worker;

/// <summary>
/// The worker's liveness health check (CORE-DR-003): healthy only when every active job loop is beating, so a
/// single hung loop is detectable. It reads the per-loop verdict from <see cref="WorkerJobHeartbeats"/> and
/// maps it to a health result the worker's <c>/health/live</c> endpoint serves.
///
/// <para>
/// Unlike the API's liveness (pure process-up, which runs no dependency checks so a failing dependency never
/// restarts a live process), the WORKER's failure mode IS a hung loop: a sweep that never returns leaves the
/// process alive yet doing no irreversible work, which must trigger a restart. So this check fails (unhealthy)
/// when any loop's heartbeat is stale, and orchestration restarts the wedged worker. The unhealthy
/// description names the stalled loops for the structured log / health UI, but the unauthenticated
/// <c>/health/live</c> response carries only the overall status (threat T7) — the same status-only posture as
/// the API health endpoints.
/// </para>
/// </summary>
internal sealed class WorkerHeartbeatLivenessHealthCheck : IHealthCheck
{
    private readonly WorkerJobHeartbeats _heartbeats;

    public WorkerHeartbeatLivenessHealthCheck(WorkerJobHeartbeats heartbeats)
    {
        ArgumentNullException.ThrowIfNull(heartbeats);
        _heartbeats = heartbeats;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var report = _heartbeats.CheckLiveness();

        var result = report.IsLive
            ? HealthCheckResult.Healthy("All worker job loops are beating.")
            : HealthCheckResult.Unhealthy(
                $"Stalled worker job loop(s): {string.Join(", ", report.StaleJobs)}.");

        return Task.FromResult(result);
    }
}
