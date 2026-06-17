// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Observability;

/// <summary>
/// Opens the per-request structured log scope (CORE-OBS-002) and emits one request-summary log line, so
/// every log line a request produces carries the documented context docs/15_OBSERVABILITY.md requires
/// (<c>request_id</c>, <c>organization_id</c>, <c>workspace_id</c>, <c>session_id</c>, <c>user_id</c>,
/// <c>event_id</c>) without leaking sensitive content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// It runs AFTER authentication and BEFORE authorization (the documented request flow
/// "authentication middleware -> tenant/workspace context resolver -> endpoint -> authorization policy",
/// docs/02_ARCHITECTURE.md), so the authenticated principal is available to seed <c>user_id</c> while the
/// scope still wraps the authorization step and the endpoint — a fail-closed <c>401</c>/<c>403</c> is logged
/// with its <c>request_id</c> too. It seeds what is known at the HTTP edge with NO database access:
/// <list type="bullet">
///   <item><c>request_id</c> — always, from the per-request correlation id.</item>
///   <item><c>user_id</c> — the authenticated principal's issuer-local subject, mapped fail-closed; an
///   anonymous or unmappable caller simply carries none. The subject is a log-safe identifier, never the
///   display name or email (threat T7).</item>
///   <item><c>workspace_id</c> / <c>session_id</c> — from the matched route, and only when the route value
///   is a well-formed surrogate <see cref="Guid"/>, so a free-form path segment can never become a log
///   value (defence against log forging).</item>
/// </list>
/// <c>organization_id</c> and <c>event_id</c> are filled in downstream by their authoritative owners (the
/// tenant context resolver and the session event publisher), because the request carries only the
/// organization slug and an event id exists only once an event is published; the mutable scope means the
/// request-summary line — logged last — carries them.
///
/// The unauthenticated infrastructure endpoints (the health probes and the metrics scrape) are skipped: they
/// carry no tenant or principal context and are polled frequently, so they are not wrapped in a per-request
/// scope (mirroring how the request-metrics middleware skips <c>/metrics</c>).
/// </summary>
internal sealed class RequestLogContextMiddleware
{
    private static readonly string[] _infrastructurePathPrefixes = ["/health", "/metrics"];

    private const string _workspaceRouteKey = "workspaceId";
    private const string _sessionRouteKey = "sessionId";
    private const string _unmatchedRoute = "(unmatched)";

    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLogContextMiddleware> _logger;

    public RequestLogContextMiddleware(RequestDelegate next, ILogger<RequestLogContextMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestLogContext logContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logContext);

        if (IsInfrastructurePath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // request_id: the per-request correlation id (CORE-OBS-005), the SAME value the X-Request-Id response
        // header carries (the well-formed inbound client id, else the active trace id, else the framework
        // TraceIdentifier), so a caller can correlate a failed response with these log lines. Resolved once and
        // cached on HttpContext.Items, so the header and the log scope can never disagree.
        logContext.SetRequestId(RequestCorrelation.Resolve(context));

        // user_id: the authenticated principal's issuer-local subject, mapped fail-closed and read with no
        // database access. An anonymous or unmappable caller carries no user_id (fail-safe).
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var mapping = OidcPrincipalMapper.Map(context.User);
            if (mapping.Succeeded)
            {
                logContext.SetUserId(mapping.Principal.SubjectId);
            }
        }

        // workspace_id / session_id: from the matched route, Guids only (never a free-form segment).
        TrySetRouteId(context, _workspaceRouteKey, logContext.SetWorkspaceId);
        TrySetRouteId(context, _sessionRouteKey, logContext.SetSessionId);

        using (_logger.BeginScope(logContext))
        {
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                // One structured request-summary line carrying the resolved context. Any log the endpoint
                // emitted within this scope already carries the same context; this line guarantees at least
                // one request log line exists and that it carries the fully resolved context. The route
                // TEMPLATE (never the concrete path or query string) keeps the message free of the ids and
                // slugs that already live in the scope (threat T7).
                _logger.LogInformation(
                    "Handled request {RequestMethod} {RequestRoute} with status {StatusCode}",
                    context.Request.Method,
                    ResolveRouteTemplate(context),
                    context.Response.StatusCode);
            }
        }
    }

    private static void TrySetRouteId(HttpContext context, string routeKey, Action<Guid> set)
    {
        if (context.Request.RouteValues.TryGetValue(routeKey, out var value)
            && Guid.TryParse(value?.ToString(), out var id))
        {
            set(id);
        }
    }

    private static bool IsInfrastructurePath(PathString path)
        => _infrastructurePathPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static string ResolveRouteTemplate(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? _unmatchedRoute;
}
