// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Diagnostics;

namespace LiveCore.Api.Observability;

/// <summary>
/// Returns the per-request correlation id to the caller (CORE-OBS-005) so a consumer can correlate a failed
/// call with the server's traces and logs. It writes two response headers on EVERY response — an authenticated
/// JSON response, a Problem Details error, a fail-closed <c>401</c>/<c>403</c> and a <c>500</c> alike:
/// <list type="bullet">
///   <item><c>X-Request-Id</c> — the <see cref="RequestCorrelation"/> id, the SAME value
///   <see cref="RequestLogContextMiddleware"/> stamps as <c>request_id</c> on every log line, so a caller that
///   reads it off a failed response can find the matching server log lines.</item>
///   <item>W3C <c>traceparent</c> — the current server span's full trace context (trace id, span id, flags),
///   so a caller can look the request up in a trace backend, and a downstream service can continue the
///   trace.</item>
/// </list>
///
/// <para>
/// It is registered EARLY (immediately after <see cref="RequestTracingMiddleware"/>, so the request span
/// exists and <c>Activity.Current</c> carries the trace context), and — like the CORE-SEC-009 security headers
/// — it applies the headers from a <c>HttpResponse.OnStarting</c> callback rather than writing them inline.
/// That is what makes the correlation id survive an error path: the global exception handler
/// (<c>UnhandledExceptionProblemDetailsMiddleware</c>) and the
/// concurrency-conflict middleware <c>Response.Clear()</c> the response before writing their Problem Details
/// body — which would wipe inline-written headers — but <c>OnStarting</c> callbacks are not cleared and fire
/// just before the (rewritten) response is sent, so the correlation id rides on the <c>500</c>/<c>409</c> too.
/// The trace context is captured WHEN the middleware runs (not inside the callback, where the ambient activity
/// may have changed), so the emitted <c>traceparent</c> always names this request's span.
/// </para>
///
/// <para>
/// THREAT T7. Both values are non-sensitive correlation identifiers — a request/trace id and a W3C trace
/// context — never a token, tenant identifier or content. The CORS policy EXPOSES both headers
/// (<c>CorsConfiguration.ExposedResponseHeaders</c>) so a cross-origin browser/PWA SDK can actually read them
/// (CORE-DX-005). The headers are added only when absent, so an upstream value (for example a reverse proxy's
/// own correlation header) is never overwritten.
/// </para>
/// </summary>
internal sealed class CorrelationHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public CorrelationHeaderMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = RequestCorrelation.Resolve(context);

        // Capture the trace context now: inside the OnStarting callback Activity.Current may have been restored
        // to an outer scope, but at this point in the pipeline it is this request's span.
        var activity = Activity.Current;
        var traceParent = activity is { IdFormat: ActivityIdFormat.W3C } ? activity.Id : null;

        context.Response.OnStarting(
            static state =>
            {
                var (response, requestId, traceParentValue) = ((HttpResponse, string, string?))state;

                if (!response.Headers.ContainsKey(RequestCorrelation.RequestIdHeaderName))
                {
                    response.Headers[RequestCorrelation.RequestIdHeaderName] = requestId;
                }

                if (traceParentValue is not null
                    && !response.Headers.ContainsKey(RequestCorrelation.TraceParentHeaderName))
                {
                    response.Headers[RequestCorrelation.TraceParentHeaderName] = traceParentValue;
                }

                return Task.CompletedTask;
            },
            (context.Response, correlationId, traceParent));

        return _next(context);
    }
}
