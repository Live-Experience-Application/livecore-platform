// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Hosting;

/// <summary>
/// Adds the baseline HTTP security response headers (CORE-SEC-009) — <c>X-Content-Type-Options</c>,
/// <c>Referrer-Policy</c> and a deny-all <c>Content-Security-Policy</c> — to EVERY response: an authenticated
/// JSON response, a Problem Details error, a 404/406/500 and the SignalR <c>/hubs/session</c> negotiate alike.
/// The exact headers and their fail-safe configuration are owned by <see cref="SecurityHeadersConfiguration"/>;
/// this middleware is only the pipeline seam that applies them.
///
/// <para>
/// It is registered EARLY (immediately after <c>UseForwardedHeaders</c>, alongside the CORE-SEC-005 transport
/// headers), so its <c>OnStarting</c> callback is enrolled for every request before any other middleware can
/// short-circuit it. It applies the headers from a <c>HttpResponse.OnStarting</c> callback rather than writing
/// them inline, which is what guarantees they survive an error path: the global exception handler
/// (<see cref="UnhandledExceptionProblemDetailsMiddleware"/>) and the concurrency-conflict middleware
/// <c>Response.Clear()</c> the response before writing their Problem Details body, which would wipe headers
/// written inline up the pipeline — but <c>OnStarting</c> callbacks are not cleared and fire just before the
/// (rewritten) response is sent, so the baseline rides on the final 500/409 too.
/// </para>
///
/// <para>
/// When the feature is disabled (<c>SecurityHeaders:Enabled=false</c>) no callback is enrolled and the request
/// passes straight through. The headers are static directive strings and carry no tenant/principal/resource
/// detail (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </para>
/// </summary>
internal sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityHeadersConfiguration.SecurityHeadersSettings _settings;

    public SecurityHeadersMiddleware(
        RequestDelegate next,
        SecurityHeadersConfiguration.SecurityHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(settings);

        _next = next;
        _settings = settings;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_settings.Enabled)
        {
            // Apply the headers when the response starts (after any Response.Clear() on an error path), so the
            // baseline rides on EVERY response regardless of status code or which middleware wrote the body. The
            // settings are passed as the callback state so the lambda captures nothing (no per-request closure).
            context.Response.OnStarting(
                static state =>
                {
                    var (response, settings) =
                        ((HttpResponse, SecurityHeadersConfiguration.SecurityHeadersSettings))state;
                    settings.Apply(response.Headers);
                    return Task.CompletedTask;
                },
                (context.Response, _settings));
        }

        return _next(context);
    }
}
