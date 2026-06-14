using System.Text;
using LiveCore.Api.Hosting;
using Microsoft.AspNetCore.Http.Features;

namespace LiveCore.Api.Store;

/// <summary>
/// HTTP endpoints of the Store module's idempotent store notification handling flow (CORE-STORE-005, the
/// "Store Notifications" epic). They realize the documented receipt-verification flow step 7
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification": "Store server notifications update
/// entitlement state on renewals, cancellations, refunds and grace periods"), so that "Renewals, cancellations,
/// refunds and grace periods update entitlements safely" (the story's acceptance criterion).
///
/// Routes owned by this story (csv/mobile_store_api_routes.csv, surfaced under the Core <c>/api/v1</c> prefix that
/// docs/08_API_CONTRACTS.md mandates for all APIs; added to csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST /api/v1/store-notifications/apple</c> — receive Apple App Store Server Notifications.</item>
///   <item><c>POST /api/v1/store-notifications/google/rtdn</c> — receive Google Real-time Developer Notifications
///   (RTDN) via Pub/Sub push.</item>
/// </list>
///
/// UNAUTHENTICATED AT THE HTTP LAYER, AUTHENTIC VIA SIGNATURE/SOURCE. A store delivers these notifications
/// server-to-server, so the routes carry no OIDC bearer token (csv/mobile_store_api_routes.csv:
/// <c>auth_required=false</c>); they are mapped with <c>AllowAnonymous</c>. The ONLY thing that makes an inbound
/// payload trustworthy is the deployment-supplied <see cref="IStoreNotificationParser"/> adapter validating its
/// signature/source ("Must validate signature/idempotency" / "Must validate source/idempotency",
/// csv/mobile_store_api_routes.csv). The flow is FAIL-CLOSED: when no parser is configured for the provider the
/// resolver throws and the request is 503, so an unauthenticated payload can NEVER change a purchase without a
/// real validator behind it (the notification analogue of the unconfigured asset storage / unconfigured purchase
/// verifier). A payload the adapter rejects as forged/unparseable is 400 and changes nothing.
///
/// IDEMPOTENT (the headline requirement, docs/21 "Store notifications must be idempotent"). The raw payload is
/// carried opaquely into the adapter, which returns a normalized <see cref="StoreNotification"/>; the
/// <see cref="StoreNotificationService"/> then deduplicates it by its (provider, provider notification id) pair
/// and drives the affected purchase's lifecycle through the idempotent, audited CORE-STORE-002 status change. A
/// re-delivered notification is acknowledged 200 with no second effect.
///
/// ACKNOWLEDGEMENT. Every notification the endpoint has safely accounted for — applied, unchanged, deduplicated,
/// not-found or ignored — is acknowledged 200 with a tiny <see cref="StoreNotificationAck"/> so the store stops
/// re-delivering it; only an unconfigured validator (503) or an unauthentic/oversize payload (400/503) is a
/// non-success the store should retry or that records nothing.
///
/// Persistence dependency: like the verification endpoints, this uses the <see cref="StoreNotificationService"/>
/// and <see cref="TimeProvider"/>, which are registered only when a database connection string is configured (see
/// <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class StoreNotificationEndpoints
{
    public static IEndpointRouteBuilder MapStoreNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // No authenticated group: a store delivers these notifications without an OIDC token. Authenticity is
        // established inside the deployment-supplied parser adapter (signature/source), not by the HTTP layer.
        // Because the surface is UNAUTHENTICATED and runs DB work plus an external parser per call, it is the
        // primary abuse/DoS target this story hardens (CORE-SEC-001): a strict PER-IP rate limit (the
        // RateLimitingConfiguration.WebhookPolicyName policy → 429 past the threshold) and a hard request-body-size
        // cap (RejectOversizeBodyAsync → 413 beyond StoreNotificationEnvelope.MaxRawPayloadLength, before the body
        // is buffered or handed to the parser).
        var group = endpoints
            .MapGroup("/api/v1/store-notifications")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingConfiguration.WebhookPolicyName)
            .AddEndpointFilter(RejectOversizeBodyAsync);

        group.MapPost("/apple", (HttpContext httpContext, CancellationToken cancellationToken)
            => HandleAsync(httpContext, PurchaseProvider.Apple, cancellationToken));

        group.MapPost("/google/rtdn", (HttpContext httpContext, CancellationToken cancellationToken)
            => HandleAsync(httpContext, PurchaseProvider.Google, cancellationToken));

        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        PurchaseProvider provider,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        // Read the opaque raw payload (bounded). It is never parsed, trusted or logged here — it is carried
        // verbatim into the provider-neutral envelope and validated only inside the adapter.
        var rawPayload = await ReadRawPayloadAsync(httpContext.Request, cancellationToken).ConfigureAwait(false);

        StoreNotificationEnvelope envelope;
        try
        {
            envelope = StoreNotificationEnvelope.Create(provider, rawPayload);
        }
        catch (ArgumentException)
        {
            // Empty or oversize body: not a notification we can validate. Records nothing.
            return ValidationError("A valid store notification payload is required.");
        }

        // Resolve the deployment-supplied validator. Fail-closed: when no adapter is configured the resolver
        // throws and the request is 503 — an unauthenticated payload never changes a purchase without a real
        // validator behind it.
        IStoreNotificationParser parser;
        try
        {
            parser = deps.Resolver.Resolve(provider);
        }
        catch (StoreNotificationParserNotConfiguredException)
        {
            return NotificationUnavailable();
        }

        // Validate the signature/source and normalize. A forged/unparseable payload is Rejected (400, records
        // nothing); an authentic but non-actionable payload is Ignored (200, records nothing).
        var parseResult = await parser.ParseAsync(envelope, cancellationToken).ConfigureAwait(false);
        switch (parseResult.Status)
        {
            case StoreNotificationParseStatus.Rejected:
                return NotificationRejected(parseResult.RejectionReason);

            case StoreNotificationParseStatus.Ignored:
                return Results.Ok(StoreNotificationAck.Ignored());

            case StoreNotificationParseStatus.Parsed:
                var now = deps.TimeProvider.GetUtcNow();
                var notification = parseResult.Notification!;

                // Reject an implausible-future event time (CORE-MON-004), recording nothing. OccurredAt is the
                // ordering key reconciliation derives a purchase's converged status from; a value far ahead of our
                // clock (a store/adapter bug or a manipulated value) would dominate that ordering, so it is
                // fail-closed rejected with the clock-skew tolerance. The early/defaulted floor is enforced upstream
                // in StoreNotification.Create.
                if (notification.OccurredAt > now + StoreNotification.MaxFutureClockSkew)
                {
                    return NotificationRejected("The store notification event time is implausibly far in the future.");
                }

                var result = await deps.Notifications
                    .HandleAsync(notification, now, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(StoreNotificationAck.From(result.Outcome));

            default:
                // Unreachable: the parse status is one of the three handled values.
                return ServiceUnavailable();
        }
    }

    /// <summary>
    /// Endpoint filter (CORE-SEC-001) that enforces a hard request-body-size cap on the anonymous webhooks BEFORE
    /// the handler reads the body or invokes the deployment-supplied parser. The cap is the configurable
    /// <see cref="RateLimitingConfiguration.RateLimitingSettings.WebhookMaxRequestBodyBytes"/>, set beyond the
    /// application-level <see cref="StoreNotificationEnvelope.MaxRawPayloadLength"/> so any legitimate notification
    /// passes while an abusive oversize body is rejected with <c>413 Payload Too Large</c> and records nothing. A
    /// declared oversize body (a <c>Content-Length</c> over the cap) is rejected up front without reading it; an
    /// undeclared/chunked body is additionally capped at the server level so Kestrel aborts a stream that runs over.
    /// </summary>
    private static async ValueTask<object?> RejectOversizeBodyAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var maxBytes = httpContext.RequestServices
            .GetRequiredService<RateLimitingConfiguration.RateLimitingSettings>()
            .WebhookMaxRequestBodyBytes;

        if (httpContext.Request.ContentLength is { } declaredLength && declaredLength > maxBytes)
        {
            return PayloadTooLarge(maxBytes);
        }

        var bodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
        {
            bodySizeFeature.MaxRequestBodySize = maxBytes;
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the request body as a bounded UTF-8 string. Reads at most one character beyond
    /// <see cref="StoreNotificationEnvelope.MaxRawPayloadLength"/> so an oversize body is detected and rejected by
    /// <see cref="StoreNotificationEnvelope.Create"/> rather than buffered without limit.
    /// </summary>
    private static async Task<string> ReadRawPayloadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var buffer = new char[StoreNotificationEnvelope.MaxRawPayloadLength + 1];
        var total = 0;
        int read;
        while (total < buffer.Length
            && (read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return new string(buffer, 0, total);
    }

    /// <summary>
    /// Resolves the dependencies from the request scope. The parser resolver is always registered; the handler
    /// service and the clock exist only when a database connection string is configured, so when persistence is
    /// off the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out StoreNotificationEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<StoreNotificationParserResolver>();
        var notifications = services.GetService<StoreNotificationService>();
        var timeProvider = services.GetService<TimeProvider>();

        if (resolver is null
            || notifications is null
            || timeProvider is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new StoreNotificationEndpointDependencies(resolver, notifications, timeProvider);
        return true;
    }

    private static IResult ServiceUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Store notification handling requires persistence, which is not configured.");

    private static IResult NotificationUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Store notification handling is not configured.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // A body larger than the configured webhook cap (CORE-SEC-001): a 413, recording nothing. The detail names
    // the byte ceiling only — never any part of the (rejected, possibly forged) payload (threat T7).
    private static IResult PayloadTooLarge(long maxBytes)
        => Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Payload Too Large",
            detail: $"A store notification payload must be at most {maxBytes} bytes.");

    // A payload the adapter could not validate as an authentic store notification (a forged/replayed/unparseable
    // body): a 400, recording nothing. The detail is the adapter's generic, log-safe reason (never the payload or
    // receipt content; threat T7); a missing reason falls back to a generic phrase.
    private static IResult NotificationRejected(string? reason)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: string.IsNullOrWhiteSpace(reason) ? "The store notification could not be validated." : reason);

    private readonly record struct StoreNotificationEndpointDependencies(
        StoreNotificationParserResolver Resolver,
        StoreNotificationService Notifications,
        TimeProvider TimeProvider);
}
