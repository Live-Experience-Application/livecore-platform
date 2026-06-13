using System.Diagnostics;

namespace LiveCore.Api.Observability;

/// <summary>
/// Times every API request and records its duration and (for server errors) its error count onto
/// <see cref="LiveCoreMetrics"/> (CORE-OBS-001) — the docs/15_OBSERVABILITY.md "API request duration" and
/// "API error rate" signals. It is the first instrumentation middleware in the pipeline so it measures the
/// WHOLE request, including authentication and authorization, and it reads the matched route AFTER the inner
/// pipeline has run (routing has executed by then), so it can tag the measurement with the route TEMPLATE
/// rather than the concrete path.
///
/// CARDINALITY AND THREAT T7. The duration is tagged with the HTTP method, the route TEMPLATE (e.g.
/// <c>/api/v1/sessions/{sessionId}/reveal</c> — never the concrete path, so a resource id is never a label)
/// and the status code; an unmatched request is tagged as <c>(unmatched)</c>. No tenant identifier, token or
/// body is ever recorded. The scrape endpoint itself (<c>/metrics</c>) is skipped so it does not measure its
/// own scrape traffic.
///
/// The error counter is incremented only for SERVER errors (5xx). The fail-closed 401/403/404 the
/// authorization model returns by design are client-side statuses and are deliberately not counted as errors
/// (they are normal, not faults), so the error rate reflects genuine server faults.
/// </summary>
internal sealed class RequestMetricsMiddleware
{
    private const string _metricsPath = "/metrics";
    private const string _unmatchedRoute = "(unmatched)";

    private readonly RequestDelegate _next;
    private readonly LiveCoreMetrics _metrics;

    public RequestMetricsMiddleware(RequestDelegate next, LiveCoreMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(metrics);
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Do not measure the scrape endpoint's own traffic.
        if (context.Request.Path.StartsWithSegments(_metricsPath, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            _metrics.RecordApiRequest(
                context.Request.Method,
                ResolveRoute(context),
                context.Response.StatusCode,
                elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Returns the matched route TEMPLATE (kept low-cardinality so a resource id never becomes a metric
    /// label), or <c>(unmatched)</c> when no endpoint matched (a 404 with no route).
    /// </summary>
    private static string ResolveRoute(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? _unmatchedRoute;
}
