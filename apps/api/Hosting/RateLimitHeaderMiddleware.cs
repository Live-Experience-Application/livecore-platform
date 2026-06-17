// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Emits the IETF draft <c>RateLimit-Limit/Remaining/Reset</c> response headers on the SUCCESS path of a request
/// the per-principal global rate limiter ADMITTED (CORE-DX-005), so a browser SDK can read its remaining
/// allowance BEFORE it is throttled — the counterpart to the same headers the limiter's <c>429</c> rejection
/// already carries (<see cref="RateLimitingConfiguration.WriteRateLimitedProblemAsync"/>).
///
/// It is registered immediately AFTER <c>UseRateLimiter</c>, so it runs INSIDE the limiter: the request's lease
/// is already held when it executes and the remaining-permit count it reports is this request's post-admission
/// snapshot, read from the SAME limiter the pipeline enforces with
/// (<see cref="RateLimiterOptions.GlobalLimiter"/>) rather than a parallel counter that could drift. The headers
/// are written before the inner pipeline runs, while the response has not started.
///
/// Only the authenticated per-principal surface carries the headers — the surface a browser SDK consumes;
/// anonymous/unthrottled traffic and a disabled limiter carry none (the helper short-circuits). The values leak
/// nothing (threat T7 in docs/07_SECURITY_THREAT_MODEL.md): a numeric ceiling, a remaining count and a window
/// length, never a tenant, principal or resource identifier. The CORS policy EXPOSES them to the browser
/// (<see cref="CorsConfiguration.ExposedResponseHeaders"/>); without that a cross-origin <c>fetch</c> could not
/// read them.
/// </summary>
internal sealed class RateLimitHeaderMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitingConfiguration.RateLimitingSettings _settings;
    private readonly PartitionedRateLimiter<HttpContext>? _globalLimiter;

    public RateLimitHeaderMiddleware(
        RequestDelegate next,
        RateLimitingConfiguration.RateLimitingSettings settings,
        IOptions<RateLimiterOptions> rateLimiterOptions)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rateLimiterOptions);

        _next = next;
        _settings = settings;

        // The exact limiter instance UseRateLimiter enforces with: the options value is a cached singleton, so
        // GetStatistics here reads the same partitions the pipeline acquires leases against.
        _globalLimiter = rateLimiterOptions.Value.GlobalLimiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The global lease is already held (this runs inside UseRateLimiter), so the reported remaining count
        // reflects THIS request's admission. A rejected request never reaches here — it short-circuits at the
        // limiter with the 429 that carries its own RateLimit-* headers.
        RateLimitingConfiguration.TryWriteAdmittedRateLimitHeaders(context, _settings, _globalLimiter);

        await _next(context).ConfigureAwait(false);
    }
}
