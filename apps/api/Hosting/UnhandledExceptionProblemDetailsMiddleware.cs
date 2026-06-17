// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Hosting;

/// <summary>
/// The global last-resort exception handler (CORE-RES-001, the "Reliability and Fault Tolerance" epic).
/// It wraps the whole endpoint pipeline and translates ANY exception that reaches it unhandled into a
/// fail-closed RFC 7807 Problem Details <c>500</c> carrying the documented <see cref="ProblemCodes.InternalError"/>
/// code (CORE-DX-001), instead of the bare framework <c>500</c> the host would otherwise return.
///
/// <para>
/// Before this middleware the pipeline (apps/api/Program.cs) had NO global exception handler: the only
/// translating middleware was <see cref="LiveCore.Api.Persistence.ConcurrencyConflictMiddleware"/>, which
/// handles a <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> (a <c>409</c>) and
/// rethrows everything else. This handler is registered OUTSIDE (before) that one, so the conflict middleware
/// still owns its <c>409</c> while every OTHER unhandled exception funnels through here — exactly the layering
/// CORE-DX-001 anticipated ("the global handler reuses the helper"). It reuses the same <see cref="CoreProblem"/>
/// factory every endpoint uses, so the body has the identical <c>application/problem+json</c> shape and stable
/// <c>code</c> member as every other error.
/// </para>
///
/// <para>
/// THREAT T7 (no content/PII leak, docs/07_SECURITY_THREAT_MODEL.md). The exception — its type, message and
/// stack — is logged SERVER-SIDE for the operator, never echoed to the caller. The response body carries only a
/// generic title/detail and the stable code; it names no exception type, resource, tenant or internal state. The
/// log line itself is identifier-only: the HTTP method, the matched route TEMPLATE (never the concrete path, so a
/// resource id is never logged) and the request's correlation id, mirroring the metrics/tracing posture
/// (CORE-OBS-001/003).
/// </para>
///
/// <para>
/// The resulting <c>500</c> is written with the response cleared, so it is a clean Problem Details body. If the
/// response has ALREADY started (the status line is on the wire and cannot be rewritten) the exception is
/// rethrown so the failure stays loud rather than being half-swallowed — the same posture
/// <see cref="LiveCore.Api.Persistence.ConcurrencyConflictMiddleware"/> takes. A client-cancellation
/// (<see cref="OperationCanceledException"/> after the request was aborted) is NOT a server fault: it is rethrown
/// untranslated so it neither synthesizes a misleading <c>500</c> nor inflates the server-error metric, and the
/// gone connection is never written to.
/// </para>
/// </summary>
internal sealed class UnhandledExceptionProblemDetailsMiddleware
{
    private const string _unmatchedRoute = "(unmatched)";

    private readonly RequestDelegate _next;
    private readonly ILogger<UnhandledExceptionProblemDetailsMiddleware> _logger;

    public UnhandledExceptionProblemDetailsMiddleware(
        RequestDelegate next,
        ILogger<UnhandledExceptionProblemDetailsMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-request. This is not a server fault, and the connection is gone so
            // nothing could be written to it — let the cancellation unwind untranslated rather than
            // synthesizing a 500 and counting it as a server error (threat T7 / CORE-OBS-001).
            throw;
        }
        catch (Exception exception)
        {
            // Diagnose server-side with the exception itself and an identifier-only context (method, route
            // TEMPLATE, correlation id) — never the concrete path or any body content (threat T7). The caller
            // never sees any of this; only the operator's logs do.
            _logger.LogError(
                exception,
                "Unhandled exception handling {Method} {Route} (request {RequestId}); returning a 500 Problem Details.",
                context.Request.Method,
                ResolveRoute(context),
                context.TraceIdentifier);

            if (context.Response.HasStarted)
            {
                // The status line is already on the wire and cannot be rewritten; rethrow so the failure stays
                // loud rather than being half-swallowed (same posture as ConcurrencyConflictMiddleware).
                throw;
            }

            context.Response.Clear();
            await CoreProblem.Create(
                statusCode: StatusCodes.Status500InternalServerError,
                code: ProblemCodes.InternalError,
                title: "Internal Server Error",
                detail: "An unexpected error occurred while processing the request.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the matched route TEMPLATE (kept low-cardinality so a resource id never reaches the log), or
    /// <c>(unmatched)</c> when no endpoint matched. Mirrors <c>RequestMetricsMiddleware.ResolveRoute</c>.
    /// </summary>
    private static string ResolveRoute(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? _unmatchedRoute;
}
