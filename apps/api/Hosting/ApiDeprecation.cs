using System.Globalization;

namespace LiveCore.Api.Hosting;

/// <summary>
/// API deprecation and sunset signaling (CORE-DX-006, the "API Contract and Consumer Ergonomics" epic). The
/// Core API is versioned by the single <c>/api/v1</c> path literal (docs/08_API_CONTRACTS.md), so its only
/// declared evolution path was a whole new version. Without an advance signal a vertical only discovers a
/// contract change when its calls break. This establishes the convention that closes that gap:
///
/// <list type="bullet">
///   <item>The default evolution rule is <b>additive-only</b>: a non-breaking change adds an OPTIONAL field, a
///   new endpoint or a new enum/event member; it never removes, renames or narrows an existing one. An additive
///   change ships under the SAME <c>/api/v1</c> version with no deprecation (docs/02_ARCHITECTURE.md,
///   docs/23_PACKAGE_VERSIONING.md).</item>
///   <item>A retiring route or field is flagged with <see cref="WithDeprecation{TBuilder}"/>, which makes the
///   endpoint emit the RFC 8594 <c>Sunset</c> header (the IMF-fixdate the resource is expected to stop
///   responding) together with the <c>Deprecation</c> header (the boolean token <c>true</c>, or the IMF-fixdate
///   the deprecation took effect when one is known), so a consumer gets the retirement date BEFORE the contract
///   changes.</item>
/// </list>
///
/// The headers are advisory metadata about the RESOURCE: they carry no tenant, principal or resource content
/// (only the route's own retirement schedule), so they leak nothing (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The CORS policy EXPOSES them
/// (<see cref="CorsConfiguration.ExposedResponseHeaders"/>) so a cross-origin browser/PWA SDK can read them, and
/// <c>@livecore/contracts</c> <c>ResponseHeaders</c> names them for a typed consumer (CORE-DX-005's pattern,
/// applied to this pair). The convention is established even though NO route is deprecated yet — the mechanism
/// and the policy exist ahead of the first deprecation.
/// </summary>
public static class ApiDeprecation
{
    /// <summary>
    /// The <c>Deprecation</c> response header name (the deprecation HTTP header field that accompanies RFC 8594
    /// <c>Sunset</c>). Present means the route is deprecated.
    /// </summary>
    public const string DeprecationHeaderName = "Deprecation";

    /// <summary>The RFC 8594 <c>Sunset</c> response header name (the date the resource is expected to retire).</summary>
    public const string SunsetHeaderName = "Sunset";

    /// <summary>
    /// The <c>Deprecation</c> header value used when only the FACT of deprecation is known (no specific
    /// deprecation date): the boolean token <c>true</c>. When a deprecation date is known the header carries that
    /// date as an IMF-fixdate instead.
    /// </summary>
    public const string DeprecatedTrueValue = "true";

    /// <summary>
    /// Flags an endpoint (a single route via <c>RouteHandlerBuilder</c>, or a whole route group via
    /// <c>RouteGroupBuilder</c>) as deprecated by attaching a <see cref="DeprecationNotice"/> to its metadata. The
    /// <see cref="DeprecationHeaderMiddleware"/> reads that metadata on a matched request and emits the
    /// <c>Deprecation</c> and <c>Sunset</c> headers. An endpoint with no notice emits neither, so the signal is
    /// strictly opt-in and never pollutes a current (non-deprecated) route.
    /// </summary>
    /// <typeparam name="TBuilder">Any endpoint convention builder (route handler or route group).</typeparam>
    /// <exception cref="ArgumentNullException">The builder or the notice is null.</exception>
    public static TBuilder WithDeprecation<TBuilder>(this TBuilder builder, DeprecationNotice notice)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(notice);

        // Add via the fundamental IEndpointConventionBuilder.Add so the notice lands on every endpoint the
        // builder produces (one route, or every route in a group).
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(notice));
        return builder;
    }

    /// <summary>
    /// Writes the <c>Deprecation</c> and <c>Sunset</c> headers for the given <paramref name="notice"/> onto the
    /// response. <c>Sunset</c> is the retirement instant as an IMF-fixdate (RFC 8594 / RFC 7231); <c>Deprecation</c>
    /// is the deprecation instant as an IMF-fixdate when known, otherwise the boolean token <c>true</c>. Setting
    /// the headers is idempotent (a repeated call overwrites rather than appends).
    /// </summary>
    /// <exception cref="ArgumentNullException">The response or the notice is null.</exception>
    public static void ApplyDeprecationHeaders(HttpResponse response, DeprecationNotice notice)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(notice);

        response.Headers[DeprecationHeaderName] = notice.DeprecatedSince is { } since
            ? since.ToString("R", CultureInfo.InvariantCulture)
            : DeprecatedTrueValue;
        response.Headers[SunsetHeaderName] = notice.SunsetAt.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Adds the <see cref="DeprecationHeaderMiddleware"/> to the pipeline so any endpoint flagged with
    /// <see cref="WithDeprecation{TBuilder}"/> emits its <c>Deprecation</c>/<c>Sunset</c> headers. It must run
    /// after routing has selected the endpoint (so the metadata is available); in the minimal-hosting pipeline
    /// routing is added at the top, so any ordinary middleware position works.
    /// </summary>
    /// <exception cref="ArgumentNullException">The application builder is null.</exception>
    public static IApplicationBuilder UseLiveCoreDeprecationHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DeprecationHeaderMiddleware>();
    }
}

/// <summary>
/// The deprecation/sunset schedule attached to a deprecated endpoint as routing metadata (CORE-DX-006). It is a
/// content-free advisory: a required <see cref="SunsetAt"/> (the RFC 8594 retirement date) and an optional
/// <see cref="DeprecatedSince"/> (the date deprecation took effect). It names no tenant, principal or resource
/// (threat T7).
/// </summary>
public sealed class DeprecationNotice
{
    /// <summary>
    /// Creates a notice for a route that retires at <paramref name="sunsetAt"/>.
    /// </summary>
    /// <param name="sunsetAt">
    /// The instant the route is expected to stop responding (RFC 8594 <c>Sunset</c>). Must be a real instant
    /// (not the default), and must be after <paramref name="deprecatedSince"/> when that is supplied.
    /// </param>
    /// <param name="deprecatedSince">
    /// The instant the route became deprecated, if known. When omitted, the <c>Deprecation</c> header carries the
    /// boolean token <c>true</c> rather than a date.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sunsetAt"/> is the default instant, or <paramref name="deprecatedSince"/> is not strictly
    /// before <paramref name="sunsetAt"/>.
    /// </exception>
    public DeprecationNotice(DateTimeOffset sunsetAt, DateTimeOffset? deprecatedSince = null)
    {
        if (sunsetAt == default)
        {
            throw new ArgumentException("A sunset instant is required.", nameof(sunsetAt));
        }

        if (deprecatedSince is { } since && since >= sunsetAt)
        {
            throw new ArgumentException(
                "Deprecation must take effect strictly before the sunset instant.",
                nameof(deprecatedSince));
        }

        SunsetAt = sunsetAt;
        DeprecatedSince = deprecatedSince;
    }

    /// <summary>The instant the route is expected to retire (RFC 8594 <c>Sunset</c>).</summary>
    public DateTimeOffset SunsetAt { get; }

    /// <summary>The instant the route became deprecated, or <see langword="null"/> when only the fact is known.</summary>
    public DateTimeOffset? DeprecatedSince { get; }
}

/// <summary>
/// Emits the <c>Deprecation</c> and <c>Sunset</c> headers (CORE-DX-006) for a request whose matched endpoint
/// carries a <see cref="DeprecationNotice"/>. It reads the endpoint selected by routing and, when a notice is
/// present, writes the headers BEFORE the inner pipeline runs (while the response has not started) — the same
/// shape as <see cref="RateLimitHeaderMiddleware"/>. An endpoint with no notice (every current route) is left
/// untouched, so the deprecation signal is strictly opt-in. The headers describe only the route's retirement
/// schedule, never tenant/principal/resource content (threat T7), and the CORS policy exposes them
/// (<see cref="CorsConfiguration.ExposedResponseHeaders"/>) so a browser SDK can read them.
/// </summary>
internal sealed class DeprecationHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public DeprecationHeaderMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Routing runs at the top of the minimal-hosting pipeline, so the matched endpoint (if any) is already
        // available here. A request that matched no endpoint, or one without a notice, carries no headers.
        var notice = context.GetEndpoint()?.Metadata.GetMetadata<DeprecationNotice>();
        if (notice is not null)
        {
            ApiDeprecation.ApplyDeprecationHeaders(context.Response, notice);
        }

        return _next(context);
    }
}
