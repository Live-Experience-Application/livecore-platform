namespace LiveCore.Api.Hosting;

/// <summary>
/// In-process mobile API gateway (CORE-MON-009, the Monetization v1 epic). The mobile-facing store and
/// entitlement routes are documented in their mobile path shape under a bare <c>/v1</c> prefix
/// (<c>csv/mobile_store_api_routes.csv</c>; <c>docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md</c> "API
/// surface"; <c>docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md</c>), but every Core HTTP endpoint is mounted
/// under the <c>/api/v1</c> prefix <c>docs/08_API_CONTRACTS.md</c> mandates. Without this gateway a mobile
/// client following the documented <c>/v1/...</c> path literally would <c>404</c>, because no endpoint is
/// mounted there.
///
/// This realizes "the mobile-facing <c>/v1/...</c> path shape resolves to the implemented <c>/api/v1</c>
/// endpoints (gateway/route prefix)" (the story's acceptance criterion) as an in-process **route prefix
/// rewrite**: a request whose path matches a documented mobile route under <c>/v1</c> has its path rewritten
/// to the corresponding <c>/api/v1</c> path BEFORE routing, so it dispatches to the SAME already-implemented
/// endpoint — authentication, tenant/subject authorization and the server-side resolution all run unchanged
/// (the rewrite touches only the request path, never the principal, the tenant boundary or the response). It
/// adds NO endpoint, service, table or migration; it is a pure addressing alias over the existing surface, so
/// a documented mobile path reaches the endpoint with no external proxy rewrite required.
///
/// SCOPED, NOT A BLANKET ALIAS (threats T1/T5). Only the EXACT documented mobile route shapes in
/// <see cref="_mobileRouteTemplates"/> (the mirror of <c>csv/mobile_store_api_routes.csv</c>) are rewritten;
/// any other <c>/v1/...</c> path is left untouched and still <c>404</c>s, so the gateway never exposes the
/// rest of the Core API under a second, undocumented prefix. The rewrite cannot widen authorization: the
/// target endpoint enforces the same fail-closed, server-side checks it always has (an anonymous caller is
/// still <c>401</c>, a service account still <c>403</c>, a foreign tenant still hidden), and the rewrite
/// neither reads nor logs the token, the body or any tenant identifier (threat T7).
///
/// PIPELINE PLACEMENT. The rewrite MUST run before endpoint routing (otherwise routing would already have
/// resolved the original, unmounted <c>/v1</c> path to "no endpoint"). It is therefore registered as an
/// <see cref="IStartupFilter"/> (<see cref="MobileApiGatewayStartupFilter"/>) rather than an ordinary
/// pipeline middleware: filter-added middleware runs ahead of the WebApplication's automatically-added
/// routing middleware, so the path is rewritten before the endpoint is selected, without re-ordering the
/// curated request pipeline in <c>Program.cs</c>.
/// </summary>
public static class MobileApiGateway
{
    /// <summary>The bare mobile-facing version prefix the store/entitlement routes are documented under.</summary>
    public const string MobileApiPrefix = "/v1";

    /// <summary>The Core prefix every HTTP endpoint is mounted under (<c>docs/08_API_CONTRACTS.md</c>).</summary>
    public const string CoreApiPrefix = "/api/v1";

    // The documented mobile routes, as path segments — the in-code mirror of csv/mobile_store_api_routes.csv
    // (and docs/21 / docs/22 "API surface"). A segment of the form "{name}" matches any single non-empty path
    // segment (a surrogate id the target endpoint then validates); every other segment must match exactly
    // (case-insensitively, as routing matches). Matching is method-agnostic: the rewrite only changes the path,
    // and the target endpoint's own routing returns 405 for a wrong method, exactly as it would under /api/v1.
    private static readonly string[][] _mobileRouteTemplates =
    [
        ["v1", "me", "entitlements"],
        ["v1", "me", "quota-status"],
        ["v1", "me", "ad-eligibility"],
        ["v1", "workspaces", "{workspaceId}", "quota-status"],
        ["v1", "purchases", "apple", "transactions"],
        ["v1", "purchases", "google", "tokens"],
        ["v1", "store-notifications", "apple"],
        ["v1", "store-notifications", "google", "rtdn"],
    ];

    /// <summary>
    /// Registers the in-process mobile API gateway: an <see cref="IStartupFilter"/> that inserts the
    /// path-rewrite middleware ahead of routing, so the documented mobile <c>/v1</c> store/entitlement paths
    /// resolve to the implemented <c>/api/v1</c> endpoints. Idempotent registration is not required; it is
    /// called once from <c>Program.cs</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services collection is null.</exception>
    public static IServiceCollection AddLiveCoreMobileApiGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IStartupFilter, MobileApiGatewayStartupFilter>();
        return services;
    }

    /// <summary>
    /// Maps a request path expressed in the mobile <c>/v1</c> shape to its Core <c>/api/v1</c> path, but ONLY
    /// when it matches one of the documented mobile routes. Returns <see langword="false"/> (leaving
    /// <paramref name="coreApiPath"/> unset) for any path that is not a documented mobile route — including a
    /// path already under <c>/api/v1</c> — so the caller leaves such requests untouched. Exposed so the
    /// route-shape mapping is unit-testable against the exact set the host serves.
    /// </summary>
    public static bool TryMapMobilePath(PathString requestPath, out PathString coreApiPath)
    {
        coreApiPath = default;

        if (!requestPath.HasValue)
        {
            return false;
        }

        var value = requestPath.Value!;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!IsDocumentedMobileRoute(segments))
        {
            return false;
        }

        // Matched, so the first segment is the "/v1" prefix; map "/v1/<suffix>" to "/api/v1/<suffix>". The
        // query string lives on Request.QueryString (not Path), so it is preserved unchanged by the caller.
        coreApiPath = new PathString(CoreApiPrefix + value[MobileApiPrefix.Length..]);
        return true;
    }

    private static bool IsDocumentedMobileRoute(string[] segments)
    {
        foreach (var template in _mobileRouteTemplates)
        {
            if (segments.Length != template.Length)
            {
                continue;
            }

            var matched = true;
            for (var i = 0; i < template.Length; i++)
            {
                var expected = template[i];
                if (IsParameterSegment(expected))
                {
                    // A surrogate-id segment: any single non-empty segment is accepted here (RemoveEmptyEntries
                    // guarantees non-empty). The target endpoint validates the value (e.g. the Guid) itself.
                    continue;
                }

                if (!string.Equals(expected, segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsParameterSegment(string templateSegment)
        => templateSegment.Length >= 2 && templateSegment[0] == '{' && templateSegment[^1] == '}';
}

/// <summary>
/// The pre-routing path-rewrite middleware behind <see cref="MobileApiGateway"/>. For a request that matches
/// a documented mobile <c>/v1</c> route it rewrites the request path to the corresponding <c>/api/v1</c> path
/// and otherwise leaves the request unchanged, then always continues the pipeline. It holds no state and
/// reads no token, body or tenant identifier (threat T7).
/// </summary>
internal sealed class MobileApiGatewayMiddleware
{
    private readonly RequestDelegate _next;

    public MobileApiGatewayMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (MobileApiGateway.TryMapMobilePath(context.Request.Path, out var coreApiPath))
        {
            context.Request.Path = coreApiPath;
        }

        return _next(context);
    }
}

/// <summary>
/// Inserts <see cref="MobileApiGatewayMiddleware"/> at the front of the request pipeline — ahead of the
/// WebApplication's automatically-added routing middleware — so the mobile <c>/v1</c> path is rewritten
/// BEFORE the endpoint is selected. Using a startup filter (rather than an ordinary <c>app.Use…</c> call)
/// guarantees the pre-routing position without otherwise re-ordering the curated <c>Program.cs</c> pipeline.
/// </summary>
internal sealed class MobileApiGatewayStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.UseMiddleware<MobileApiGatewayMiddleware>();
            next(app);
        };
    }
}
