using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LiveCore.Api;

/// <summary>
/// Maps the health endpoints of the API host (CORE-FND-004).
///
/// The endpoints are unauthenticated by convention (orchestrators and load
/// balancers probe them before any auth flow exists), so the response is
/// deliberately minimal: only the overall status. No version numbers,
/// configuration values, host names or individual check details are exposed
/// (see docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal static class HealthEndpoints
{
    /// <summary>
    /// Health checks tagged with this value gate readiness. Later stories
    /// (database, storage, realtime backplane) register their checks with
    /// this tag; the skeleton registers none yet.
    /// </summary>
    internal const string ReadinessTag = "ready";

    public static IEndpointRouteBuilder MapLiveCoreHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: the process is up and able to serve HTTP. It runs no
        // registered checks on purpose, so a failing dependency can never
        // make an orchestrator restart an otherwise healthy process.
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteStatusAsync,
        });

        // Readiness: the process is ready to receive traffic. Runs only the
        // checks tagged as readiness-relevant (none registered yet).
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadinessTag),
            ResponseWriter = WriteStatusAsync,
        });

        return endpoints;
    }

    private static Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        // Only the overall status leaves the process. Check names, durations,
        // descriptions and exception details stay server-side; they belong in
        // structured logs, not in an unauthenticated HTTP response.
        return context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() });
    }
}
