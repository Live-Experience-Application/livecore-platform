// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Worker;

/// <summary>
/// Maps the worker host's liveness and readiness endpoints (CORE-DR-003; readiness added by CORE-OBS-013).
/// Before this story the worker served no HTTP traffic and its only health signal was a single shared heartbeat
/// file; now that the worker exposes an HTTP surface for its metrics
/// (<see cref="LiveCore.Api.Observability.MetricsEndpointRouteBuilderExtensions"/>) it serves both
/// <c>/health/live</c> and <c>/health/ready</c> — exactly as the API host exposes the same two endpoints.
///
/// <para>
/// The two signals are DISTINCT, the same split the API host makes:
/// <list type="bullet">
///   <item><c>/health/live</c> — the PER-LOOP liveness check (<see cref="WorkerHeartbeatLivenessHealthCheck"/>):
///   a single hung loop is detectable over a single httpGet probe so orchestration restarts a WEDGED worker.
///   With no active loop it is VACUOUSLY healthy — a process with nothing to do is genuinely alive, and a
///   restart would not help.</item>
///   <item><c>/health/ready</c> — the readiness gate (<see cref="WorkerReadiness"/> plus the production-config
///   backstop <see cref="WorkerProductionConfiguration"/>): in Production a worker that can do no work (no
///   persistence / zero active loops) or whose configuration is malformed reports NOT-READY, so it is NOT
///   vacuously healthy. Liveness restarts a wedged worker; readiness keeps a worker-that-cannot-work out of the
///   ready rotation without pointlessly restarting it.</item>
/// </list>
/// </para>
///
/// Both endpoints are unauthenticated by convention (orchestration probes them before any auth flow), so the
/// response is deliberately minimal: only the overall status. No loop names, timestamps, configuration values
/// or check details are exposed (docs/07_SECURITY_THREAT_MODEL.md, threat T7); those stay in the structured
/// log. A deployment restricts the surface at the network edge, exactly like the API's <c>/health/*</c> and
/// <c>/metrics</c> (docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </summary>
internal static class WorkerHealthEndpoints
{
    /// <summary>Health checks tagged with this value gate the worker's liveness endpoint.</summary>
    internal const string LivenessTag = "live";

    /// <summary>Health checks tagged with this value gate the worker's readiness endpoint (CORE-OBS-013).</summary>
    internal const string ReadinessTag = "ready";

    public static IEndpointRouteBuilder MapWorkerHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Liveness: every active job loop is beating. Runs only the liveness-tagged check, so a hung loop
        // makes the worker not-live and orchestration restarts it; with no loops it is vacuously healthy.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LivenessTag),
            ResponseWriter = WriteStatusAsync,
        });

        // Readiness: the worker can actually do its job. Runs only the readiness-tagged checks (the
        // required-dependency / active-loop gate and the malformed-config backstop), so a worker with no work to
        // do or a malformed configuration reports NOT-READY in Production rather than being vacuously healthy.
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteStatusAsync,
        });

        return endpoints;
    }

    private static Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        // Only the overall status leaves the process. Check names, durations, descriptions (including the
        // stalled loop names) and exception details stay server-side; they belong in structured logs, not in
        // an unauthenticated HTTP response (threat T7).
        return context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
}
