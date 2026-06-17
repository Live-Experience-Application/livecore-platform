namespace LiveCore.Api.Hosting;

/// <summary>
/// Cross-Origin Resource Sharing (CORS) wiring for browser/PWA clients (CORE-OPS-003, the "Production
/// Operations Readiness" epic). A browser served from a different origin than the Core API (the PWA
/// front-end, docs/02_ARCHITECTURE.md "Next.js App Router / PWA-first") may only call the REST API and
/// the SignalR hub when its origin is on a configured allow-list — the same allow-list applied to both
/// surfaces through one named policy (<see cref="PolicyName"/>).
///
/// The allow-list is read from configuration only (<c>Cors:AllowedOrigins</c>), exactly like the other
/// runtime settings (the OIDC <c>Authentication:Oidc:*</c> keys, the storage <c>Assets:Storage:*</c>
/// keys); docs/13_SELF_HOSTING_REQUIREMENTS.md lists "CORS allowed origins" as runtime configuration, so
/// nothing about which sites may call the API is hardcoded.
///
/// FAIL-CLOSED by default: with no configured origins the policy allows NO cross-origin browser client.
/// A disallowed origin's preflight receives no <c>Access-Control-Allow-Origin</c> header, so the browser
/// blocks the call. CORS never widens server-side authorization — it is a browser-enforced boundary on
/// top of the OIDC/tenant checks every endpoint already applies (docs/07_SECURITY_THREAT_MODEL.md); an
/// allowed origin still presents a valid token and passes the same authorization. Because the allow-list
/// is always an explicit set of origins (never a wildcard), credentialed requests are permitted, which a
/// browser SignalR client needs.
/// </summary>
public static class CorsConfiguration
{
    /// <summary>Configuration section that carries the CORS settings (<c>Cors</c>).</summary>
    public const string ConfigurationSection = "Cors";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the allow-list.</summary>
    public const string AllowedOriginsKey = "AllowedOrigins";

    /// <summary>
    /// Name of the single CORS policy applied to both the REST API and the SignalR hub. A generic
    /// transport name (not a vertical or feature name; AGENTS.md).
    /// </summary>
    public const string PolicyName = "LiveCoreBrowserClients";

    /// <summary>
    /// Name of the response header carrying the per-request correlation/trace id a browser SDK reads to
    /// correlate a failed call with server logs/traces (CORE-OBS-005). It mirrors the documented
    /// <c>X-Request-Id</c> request header a client may also send (docs/08_API_CONTRACTS.md "Common headers"); a
    /// correlation token is a non-sensitive identifier, never content (threat T7).
    /// </summary>
    public const string RequestIdHeaderName = "X-Request-Id";

    /// <summary>
    /// The response headers a cross-origin browser/PWA SDK must be able to READ (CORE-DX-005). By default a
    /// <c>fetch</c> from a different origin can read only the CORS "simple" response headers; any other header
    /// is invisible to the SDK unless CORS explicitly exposes it (the <c>Access-Control-Expose-Headers</c>
    /// response header). Without this list a browser SDK silently cannot read the weak optimistic-concurrency
    /// <c>ETag</c> (CORE-DX-002), the <c>Retry-After</c> and <c>RateLimit-*</c> rate-limit signals
    /// (CORE-SEC-001 / CORE-DX-005), the <c>Location</c> of a freshly created resource, the request-id
    /// correlation header (CORE-OBS-005), or the <c>Deprecation</c>/<c>Sunset</c> retirement signal on a
    /// deprecated route (CORE-DX-006). None of these leak tenant/principal content (threat T7): they are an
    /// opaque version validator, a numeric ceiling/remaining/reset, a created-resource path, a correlation id and
    /// a route's own retirement schedule.
    /// </summary>
    public static readonly IReadOnlyList<string> ExposedResponseHeaders =
    [
        "ETag",
        "Location",
        "Retry-After",
        RequestIdHeaderName,
        RateLimitingConfiguration.RateLimitLimitHeaderName,
        RateLimitingConfiguration.RateLimitRemainingHeaderName,
        RateLimitingConfiguration.RateLimitResetHeaderName,
        ApiDeprecation.DeprecationHeaderName,
        ApiDeprecation.SunsetHeaderName,
    ];

    /// <summary>
    /// Reads and normalizes the configured allow-list under
    /// <c>Cors:AllowedOrigins</c> (<c>Cors:AllowedOrigins:0</c>, <c>:1</c>, …). Each entry is trimmed, has
    /// any trailing slash removed (a CORS origin is scheme+host[+port] with no path) and blanks are
    /// dropped; duplicates are removed case-insensitively. Returns an empty list when nothing is
    /// configured — the fail-closed default (no origin allowed).
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> ReadAllowedOrigins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration
            .GetSection($"{ConfigurationSection}:{AllowedOriginsKey}")
            .Get<string[]>() ?? [];

        var normalized = new List<string>();
        foreach (var origin in configured)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                continue;
            }

            var trimmed = origin.Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!normalized.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Registers the single named CORS policy (<see cref="PolicyName"/>) from the configured allow-list.
    /// When the allow-list is empty the policy is left with no permitted origin (fail-closed): every
    /// cross-origin request is denied. When origins are configured the policy permits exactly those
    /// origins with any method and header and with credentials (valid because the origins are an explicit
    /// set, never a wildcard), which a browser SignalR client requires for the <c>/hubs</c> connection, and
    /// EXPOSES the <see cref="ExposedResponseHeaders"/> so a browser SDK can read the rate-limit, concurrency,
    /// correlation and created-resource response headers (CORE-DX-005).
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The allow-list is read inside the (lazily-invoked) options setup so it
        // reflects the fully-assembled configuration when the policy is built, not
        // whatever sources happened to be present at registration time.
        services.AddCors(options =>
        {
            var origins = ReadAllowedOrigins(configuration).ToArray();

            options.AddPolicy(PolicyName, policy =>
            {
                if (origins.Length == 0)
                {
                    // Fail-closed: no configured origins => no origin is permitted. A
                    // disallowed origin's preflight gets no Access-Control-Allow-Origin
                    // header and the browser blocks the call.
                    return;
                }

                policy
                    .WithOrigins(origins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders([.. ExposedResponseHeaders])
                    .AllowCredentials();
            });
        });

        return services;
    }
}
