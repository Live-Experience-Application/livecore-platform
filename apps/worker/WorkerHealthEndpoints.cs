using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Worker;

/// <summary>
/// Maps the worker host's liveness endpoint (CORE-DR-003). Before this story the worker served no HTTP traffic
/// and its liveness signal was a single shared heartbeat file; now that the worker exposes an HTTP surface for
/// its metrics (<see cref="LiveCore.Api.Observability.MetricsEndpointRouteBuilderExtensions"/>) it serves
/// <c>/health/live</c> too, backed by the per-loop liveness check
/// (<see cref="WorkerHeartbeatLivenessHealthCheck"/>) so a single hung loop is detectable over a single
/// httpGet probe — exactly as the API host exposes <c>/health/live</c>.
///
/// The endpoint is unauthenticated by convention (orchestration probes it before any auth flow), so the
/// response is deliberately minimal: only the overall status. No loop names, timestamps, configuration values
/// or check details are exposed (docs/07_SECURITY_THREAT_MODEL.md, threat T7); those stay in the structured
/// log. A deployment restricts the surface at the network edge, exactly like the API's <c>/health/*</c> and
/// <c>/metrics</c> (docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </summary>
internal static class WorkerHealthEndpoints
{
    /// <summary>Health checks tagged with this value gate the worker's liveness endpoint.</summary>
    internal const string LivenessTag = "live";

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
