using System.Globalization;
using System.Threading.RateLimiting;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Store;
using Microsoft.AspNetCore.RateLimiting;

namespace LiveCore.Api.Hosting;

/// <summary>
/// Request rate limiting for the API host (CORE-SEC-001, the "Security Hardening" epic). Before this story the
/// HTTP pipeline applied no rate limiting anywhere (a grep for <c>RateLimit</c>/<c>AddRateLimiter</c>/
/// <c>UseRateLimiter</c> returned zero), leaving two clear abuse/DoS surfaces unbounded: the two
/// <see cref="StoreNotificationEndpoints">store-notification webhooks</see> are <c>AllowAnonymous</c>
/// server-to-server callbacks (csv/mobile_store_api_routes.csv: <c>auth_required=false</c>) that do database work
/// and run a deployment-supplied external parser per call — a ledger-amplification and enumeration surface anyone
/// can POST to — and every authenticated endpoint could be hammered without a per-caller ceiling.
///
/// This wires ASP.NET Core's built-in rate limiting (<c>Microsoft.AspNetCore.RateLimiting</c> /
/// <c>System.Threading.RateLimiting</c>, part of the shared framework — NO new dependency) in two complementary
/// shapes, both fixed-window and both FAIL-CLOSED-friendly:
/// <list type="bullet">
///   <item>A STRICT PER-IP limit on the anonymous webhooks, applied by the named <see cref="WebhookPolicyName"/>
///   policy the webhook route group opts into with <c>RequireRateLimiting</c>. The partition key is the real
///   client IP — restored by <c>UseForwardedHeaders</c> (which runs first in the pipeline) from a TRUSTED proxy,
///   so the limit follows the actual caller, not the proxy hop (threat T7).</item>
///   <item>A PER-PRINCIPAL GLOBAL limiter on the authenticated surface, applied as the
///   <see cref="RateLimiterOptions.GlobalLimiter"/>. It partitions on the authenticated principal's issuer+subject
///   pair (subjects are unique only per issuer, <see cref="OidcClaimTypes"/>), so one caller's burst cannot
///   exhaust another's allowance (threat T5). Anonymous traffic (the health/metrics endpoints and the anonymous
///   webhooks) is intentionally NOT throttled by the global limiter — the webhook per-IP policy guards the
///   anonymous webhook surface, and the infrastructure endpoints stay reachable for probes/scrapes.</item>
/// </list>
///
/// Every rejected request gets <c>429 Too Many Requests</c> as RFC 7807 Problem Details (the documented
/// <c>429</c> "rate limited" code, docs/08_API_CONTRACTS.md) with a <c>Retry-After</c> header and no tenant,
/// principal or resource detail in the body (threat T7). Every limit is CONFIGURABLE from configuration only
/// (<c>RateLimiting:*</c>; docs/13_SELF_HOSTING_REQUIREMENTS.md), with safe, generous defaults so normal traffic
/// is unaffected, and the whole feature can be turned off (<c>RateLimiting:Enabled=false</c>) for a deployment
/// that throttles at its edge instead — in which case both limiters become no-ops and the middleware is inert.
/// A misconfigured non-positive limit/window falls back to the default rather than crashing the limiter
/// (fail-safe to the documented ceiling, never to "no limit").
///
/// Rate limiting is a coarse abuse/DoS ceiling layered ON TOP OF the OIDC/tenant authorization every endpoint
/// already enforces server-side; it never widens authorization (docs/07_SECURITY_THREAT_MODEL.md), exactly as
/// the CORS allow-list (<see cref="CorsConfiguration"/>) is a boundary layered on the same checks.
/// </summary>
public static class RateLimitingConfiguration
{
    /// <summary>Configuration section that carries the rate-limiting settings (<c>RateLimiting</c>).</summary>
    public const string ConfigurationSection = "RateLimiting";

    /// <summary>
    /// Name of the named policy applied to the anonymous store-notification webhook route group (a generic
    /// transport name, not a vertical or feature name; AGENTS.md).
    /// </summary>
    public const string WebhookPolicyName = "LiveCoreStoreNotificationWebhooks";

    /// <summary>
    /// Response header carrying the request quota (permit ceiling) for the window the caller is in — the IETF
    /// draft <c>RateLimit</c> header field family (CORE-DX-005). Emitted on both an admitted response and a
    /// <c>429</c> rejection. A numeric ceiling, never a tenant/principal identifier (threat T7).
    /// </summary>
    public const string RateLimitLimitHeaderName = "RateLimit-Limit";

    /// <summary>
    /// Response header carrying the permits remaining in the caller's current window (<c>0</c> on a <c>429</c>).
    /// </summary>
    public const string RateLimitRemainingHeaderName = "RateLimit-Remaining";

    /// <summary>
    /// Response header carrying the seconds until the caller's current window resets and the quota refills.
    /// </summary>
    public const string RateLimitResetHeaderName = "RateLimit-Reset";

    /// <summary>Default per-principal global ceiling: permits per window.</summary>
    public const int DefaultGlobalPermitLimit = 300;

    /// <summary>Default per-principal global window, in seconds.</summary>
    public const int DefaultGlobalWindowSeconds = 60;

    /// <summary>Default per-IP webhook ceiling: permits per window.</summary>
    public const int DefaultWebhookPermitLimit = 60;

    /// <summary>Default per-IP webhook window, in seconds.</summary>
    public const int DefaultWebhookWindowSeconds = 60;

    /// <summary>
    /// Default hard request-body-size cap on the webhooks, in bytes. Set BEYOND
    /// <see cref="StoreNotificationEnvelope.MaxRawPayloadLength"/> (the application-level character cap on the
    /// payload itself) so a body comfortably larger than any legitimate notification is rejected at the HTTP
    /// layer before it is buffered or handed to the per-call external parser.
    /// </summary>
    public const long DefaultWebhookMaxRequestBodyBytes = StoreNotificationEnvelope.MaxRawPayloadLength * 2L;

    // Stable partition keys for the no-limiter (unthrottled) cases, so all unthrottled requests share one inert
    // partition rather than allocating a limiter per key.
    private const string _unthrottledPartitionKey = "livecore:unthrottled";

    /// <summary>
    /// The resolved, validated rate-limiting settings. Exposed (with <see cref="Read"/>) so the wiring is testable
    /// against the exact values the host runs with, exactly like <see cref="ForwardedHeadersConfiguration.Apply"/>.
    /// </summary>
    public sealed record RateLimitingSettings(
        bool Enabled,
        int GlobalPermitLimit,
        int GlobalWindowSeconds,
        int GlobalQueueLimit,
        int WebhookPermitLimit,
        int WebhookWindowSeconds,
        int WebhookQueueLimit,
        long WebhookMaxRequestBodyBytes)
    {
        /// <summary>
        /// Reads the rate-limiting settings from <see cref="ConfigurationSection"/>. Rate limiting is ENABLED by
        /// default; each limit/window defaults to the documented ceiling and a non-positive configured value falls
        /// back to that default (fail-safe — a misconfiguration never disables a limit silently). The queue limits
        /// default to zero (excess requests are rejected immediately rather than queued).
        /// </summary>
        /// <exception cref="ArgumentNullException">The configuration is null.</exception>
        public static RateLimitingSettings Read(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(ConfigurationSection);

            return new RateLimitingSettings(
                Enabled: section.GetValue("Enabled", true),
                GlobalPermitLimit: PositiveOrDefault(section.GetValue<int?>("Global:PermitLimit"), DefaultGlobalPermitLimit),
                GlobalWindowSeconds: PositiveOrDefault(section.GetValue<int?>("Global:WindowSeconds"), DefaultGlobalWindowSeconds),
                GlobalQueueLimit: NonNegativeOrDefault(section.GetValue<int?>("Global:QueueLimit"), 0),
                WebhookPermitLimit: PositiveOrDefault(section.GetValue<int?>("Webhooks:PermitLimit"), DefaultWebhookPermitLimit),
                WebhookWindowSeconds: PositiveOrDefault(section.GetValue<int?>("Webhooks:WindowSeconds"), DefaultWebhookWindowSeconds),
                WebhookQueueLimit: NonNegativeOrDefault(section.GetValue<int?>("Webhooks:QueueLimit"), 0),
                WebhookMaxRequestBodyBytes: PositiveOrDefault(section.GetValue<long?>("Webhooks:MaxRequestBodyBytes"), DefaultWebhookMaxRequestBodyBytes));
        }

        private static int PositiveOrDefault(int? value, int fallback)
            => value is { } v && v > 0 ? v : fallback;

        private static long PositiveOrDefault(long? value, long fallback)
            => value is { } v && v > 0 ? v : fallback;

        private static int NonNegativeOrDefault(int? value, int fallback)
            => value is { } v && v >= 0 ? v : fallback;
    }

    /// <summary>
    /// Registers the rate limiter (the per-principal global limiter and the per-IP webhook policy) plus the
    /// resolved <see cref="RateLimitingSettings"/>. The settings are read LAZILY (inside the options setup and
    /// the singleton factory) so deployment configuration assembled after registration is reflected — the same
    /// pattern <see cref="CorsConfiguration.AddLiveCoreCors"/> uses.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Resolved lazily from the fully-assembled configuration so the webhook body-size cap (consumed by the
        // webhook endpoint filter) reflects any source added after registration.
        services.AddSingleton(sp => RateLimitingSettings.Read(sp.GetRequiredService<IConfiguration>()));

        services.AddRateLimiter(options =>
        {
            var settings = RateLimitingSettings.Read(configuration);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (rejected, token) => WriteRateLimitedProblemAsync(rejected, settings, token);

            // Per-principal global limiter on the authenticated surface.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter(_unthrottledPartitionKey);
                }

                var principalKey = TryGetPrincipalPartitionKey(context);
                if (principalKey is null)
                {
                    // Anonymous traffic (health/metrics, the anonymous webhooks) is not throttled here; the
                    // webhook per-IP policy below guards the anonymous webhook surface.
                    return RateLimitPartition.GetNoLimiter(_unthrottledPartitionKey);
                }

                return RateLimitPartition.GetFixedWindowLimiter(principalKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.GlobalPermitLimit,
                    Window = TimeSpan.FromSeconds(settings.GlobalWindowSeconds),
                    QueueLimit = settings.GlobalQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });

            // Strict per-IP limit on the anonymous store-notification webhooks. Always registered (even when
            // disabled) so the route group's RequireRateLimiting(WebhookPolicyName) always resolves a policy.
            options.AddPolicy(WebhookPolicyName, context =>
            {
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter(_unthrottledPartitionKey);
                }

                return RateLimitPartition.GetFixedWindowLimiter(GetClientIpPartitionKey(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.WebhookPermitLimit,
                    Window = TimeSpan.FromSeconds(settings.WebhookWindowSeconds),
                    QueueLimit = settings.WebhookQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });

        return services;
    }

    /// <summary>
    /// Builds the per-principal partition key, or <c>null</c> for an unauthenticated request (which the global
    /// limiter leaves unthrottled). The key is the issuer+subject pair because an OIDC subject is unique only per
    /// issuer (<see cref="OidcClaimTypes"/>), so two principals from different issuers never collide.
    /// </summary>
    private static string? TryGetPrincipalPartitionKey(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = user.FindFirst(OidcClaimTypes.Subject)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var issuer = user.FindFirst(OidcClaimTypes.Issuer)?.Value ?? string.Empty;
        return $"principal:{issuer}|{subject}";
    }

    /// <summary>
    /// Builds the per-IP partition key for the webhook policy. Uses the connection's remote IP — restored to the
    /// real client by <c>UseForwardedHeaders</c> when behind a trusted proxy — falling back to a shared key when
    /// the address is unavailable (so an address-less peer is still bounded, never unlimited).
    /// </summary>
    private static string GetClientIpPartitionKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null ? "ip:unknown" : $"ip:{address}";
    }

    /// <summary>
    /// Writes the fail-closed <c>429</c> response: an RFC 7807 Problem Details body (the same shape every other
    /// endpoint returns via <c>Results.Problem</c>) plus a <c>Retry-After</c> header derived from the limiter
    /// lease and the IETF draft <c>RateLimit-Limit/Remaining/Reset</c> headers (CORE-DX-005). The body and the
    /// headers carry no tenant, principal or resource detail — only a numeric ceiling, a remaining count of
    /// <c>0</c> and a reset delay (threat T7).
    /// </summary>
    private static ValueTask WriteRateLimitedProblemAsync(
        OnRejectedContext context,
        RateLimitingSettings settings,
        CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var response = httpContext.Response;
        if (response.HasStarted)
        {
            return ValueTask.CompletedTask;
        }

        // Map the rejection back to the limit that applied: the per-principal global limiter only ever throttles
        // an authenticated principal, the per-IP webhook policy only the anonymous webhooks, so the principal
        // check selects the right ceiling/window without leaking which partition was hit.
        var isPrincipal = TryGetPrincipalPartitionKey(httpContext) is not null;
        var limit = isPrincipal ? settings.GlobalPermitLimit : settings.WebhookPermitLimit;
        var resetSeconds = isPrincipal ? settings.GlobalWindowSeconds : settings.WebhookWindowSeconds;

        // The lease's RetryAfter is the exact seconds until the window rolls; it is both the Retry-After value
        // and the RateLimit-Reset value for a rejected request.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            resetSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            response.Headers.RetryAfter = resetSeconds.ToString(CultureInfo.InvariantCulture);
        }

        WriteRateLimitHeaders(response, limit, remaining: 0, resetSeconds);

        return new ValueTask(CoreProblem.Create(
            statusCode: StatusCodes.Status429TooManyRequests,
            code: ProblemCodes.RateLimited,
            title: "Too Many Requests",
            detail: "The request was rejected because a rate limit was exceeded. Retry after a short delay.")
            .ExecuteAsync(httpContext));
    }

    /// <summary>
    /// Emits the IETF draft <c>RateLimit-Limit/Remaining/Reset</c> headers for a request the per-principal
    /// global limiter ADMITTED — the success-path counterpart to the same headers a <c>429</c> rejection carries
    /// (CORE-DX-005). The remaining count is read from the SAME limiter the pipeline enforces with (no parallel
    /// counter): <see cref="RateLimitHeaderMiddleware"/> calls this while the request's lease is still held, so
    /// the value is this request's post-admission snapshot. Only the authenticated per-principal surface — the
    /// surface a browser SDK consumes — carries the headers; an anonymous/unthrottled request, a disabled
    /// limiter or an already-started response is left untouched. The values leak nothing (threat T7): a numeric
    /// ceiling, a remaining count and a window length.
    /// </summary>
    internal static void TryWriteAdmittedRateLimitHeaders(
        HttpContext context,
        RateLimitingSettings settings,
        PartitionedRateLimiter<HttpContext>? globalLimiter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled || globalLimiter is null)
        {
            return;
        }

        var response = context.Response;
        if (response.HasStarted)
        {
            return;
        }

        // Only the per-principal authenticated surface is bounded by the global limiter; an anonymous request is
        // left unthrottled there, so it carries no RateLimit-* on the success path.
        if (TryGetPrincipalPartitionKey(context) is null)
        {
            return;
        }

        var statistics = globalLimiter.GetStatistics(context);
        if (statistics is null)
        {
            return;
        }

        WriteRateLimitHeaders(
            response,
            limit: settings.GlobalPermitLimit,
            remaining: statistics.CurrentAvailablePermits,
            resetSeconds: settings.GlobalWindowSeconds);
    }

    /// <summary>
    /// Sets the three <c>RateLimit-*</c> headers from a ceiling, a remaining count (clamped to be non-negative)
    /// and a reset delay in seconds, all formatted invariantly. Shared by the admitted-path helper and the
    /// rejection writer so both surfaces emit an identical header shape.
    /// </summary>
    private static void WriteRateLimitHeaders(HttpResponse response, int limit, long remaining, int resetSeconds)
    {
        var clampedRemaining = remaining < 0 ? 0 : remaining;
        response.Headers[RateLimitLimitHeaderName] = limit.ToString(CultureInfo.InvariantCulture);
        response.Headers[RateLimitRemainingHeaderName] = clampedRemaining.ToString(CultureInfo.InvariantCulture);
        response.Headers[RateLimitResetHeaderName] = resetSeconds.ToString(CultureInfo.InvariantCulture);
    }
}
