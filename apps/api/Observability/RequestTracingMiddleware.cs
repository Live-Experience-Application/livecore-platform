namespace LiveCore.Api.Observability;

/// <summary>
/// Produces one distributed-tracing SERVER span per API request through <see cref="LiveCoreActivitySource"/>
/// (CORE-OBS-003) — the docs/15_OBSERVABILITY.md "Key request flow" span. It starts the span at the top of the
/// application pipeline so the span WRAPS the whole request (authentication, authorization and the endpoint),
/// which makes every span produced deeper in the request — the reveal span and its session-event publish
/// spans — nest under it automatically (they parent to <c>Activity.Current</c>). It reads the matched route
/// AFTER the inner pipeline has run (routing has executed by then), so it names and tags the span with the
/// route TEMPLATE rather than the concrete path.
///
/// CARDINALITY AND THREAT T7. The span is named "{method} {route TEMPLATE}" and tagged with the HTTP method,
/// the route TEMPLATE (e.g. <c>/api/v1/sessions/{sessionId}/reveal</c> — never the concrete path, so a
/// resource id is never a tag) and the status code; an unmatched request is tagged <c>(unmatched)</c>. No
/// access token, tenant identifier or body is ever attached. The unauthenticated infrastructure endpoints
/// (<c>/health/*</c>, <c>/metrics</c>) are skipped — they are polled frequently and carry no request context
/// worth tracing — mirroring how the log-context middleware skips them.
///
/// When nothing is listening to the source (no tracer provider / exporter wired) starting the span is a
/// no-op that returns <see langword="null"/>, so the middleware just forwards the request — tracing is free
/// when off.
/// </summary>
internal sealed class RequestTracingMiddleware
{
    private static readonly string[] _infrastructurePathPrefixes = ["/health", "/metrics"];

    private const string _httpMethodTag = "http.request.method";
    private const string _httpRouteTag = "http.route";
    private const string _httpStatusCodeTag = "http.response.status_code";
    private const string _unmatchedRoute = "(unmatched)";

    private readonly RequestDelegate _next;
    private readonly LiveCoreActivitySource _activitySource;

    public RequestTracingMiddleware(RequestDelegate next, LiveCoreActivitySource activitySource)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(activitySource);
        _next = next;
        _activitySource = activitySource;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Skip the frequently-polled, context-free infrastructure endpoints so they do not flood the trace
        // stream (the same paths the log-context middleware skips).
        if (IsInfrastructurePath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        using var activity = _activitySource.StartHttpServerRequest();

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            // Routing has run by now, so the route TEMPLATE is available. Name and tag the span only when one
            // was actually created (a listener is attached).
            if (activity is not null)
            {
                var route = ResolveRoute(context);
                activity.DisplayName = $"{context.Request.Method} {route}";
                activity.SetTag(_httpMethodTag, context.Request.Method);
                activity.SetTag(_httpRouteTag, route);
                activity.SetTag(_httpStatusCodeTag, context.Response.StatusCode);
            }
        }
    }

    private static bool IsInfrastructurePath(PathString path)
        => _infrastructurePathPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the matched route TEMPLATE (kept low-cardinality so a resource id never becomes a span tag),
    /// or <c>(unmatched)</c> when no endpoint matched (a 404 with no route).
    /// </summary>
    private static string ResolveRoute(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? _unmatchedRoute;
}
