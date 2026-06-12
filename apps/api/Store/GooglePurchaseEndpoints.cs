using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Store;

/// <summary>
/// HTTP endpoint of the Store module's Google purchase token verification flow (CORE-STORE-004, the Google
/// verification endpoint contract story of the "Store Purchase Verification" epic). It is the Google analogue of
/// <see cref="ApplePurchaseEndpoints"/> (CORE-STORE-003): same verify-then-record, fail-closed shape, differing
/// only in the provider (<see cref="PurchaseProvider.Google"/>) and the proof's name (a Google Play purchase
/// token rather than an Apple signed transaction JWS). It realizes the documented receipt-verification flow
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification": "Mobile app sends transaction
/// token/JWS/purchase token to backend; Backend verifies with Apple/Google server APIs; Backend persists
/// PurchaseTransaction") and the documented request flow (authentication → endpoint → authorization → command,
/// docs/02_ARCHITECTURE.md), mirroring the Apple, asset and quota-status endpoints.
///
/// Route owned by this story (csv/mobile_store_api_routes.csv, surfaced under the Core <c>/api/v1</c> prefix that
/// docs/08_API_CONTRACTS.md mandates for all APIs; added to csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST /api/v1/purchases/google/tokens</c> — submit a Google Play purchase token for server-side
///   verification. Authorized to any authenticated USER principal (a mobile buyer submitting their own
///   purchase); a service account has no personal purchase and is 403.</item>
/// </list>
///
/// THE EPIC STORY'S ACCEPTANCE CRITERION — "Google purchase tokens are verified before entitlements are granted."
/// The flow is fail-closed and verify-then-record: the submitted token is verified against Google's server APIs
/// through the deployment-supplied <see cref="IPurchaseVerificationProvider"/> adapter (resolved by
/// <see cref="PurchaseVerificationProviderResolver"/>) and ONLY a verified result is persisted as a
/// <see cref="PurchaseTransaction"/> (reusing the CORE-STORE-002 <see cref="PurchaseTransactionService"/>). A
/// rejected (forged / replayed / unverifiable) token records NOTHING and grants nothing — Core never trusts a
/// client's premium claim ("Never trust client-side premium flags"; "Never unlock limits before server
/// verification succeeds", docs/21). Granting the resulting <c>SubjectEntitlement</c> from the recorded purchase
/// (the product → plan → entitlement mapping) and linking the buyer (<c>billing_account_links</c>) are later
/// stories; this story establishes the verify-and-record gate they sit behind.
///
/// Authorization model (server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>The bearer middleware challenges a missing/invalid token with 401 before any handler runs; a
///   principal that cannot be mapped fail-closed from the request's claims is also 401.</item>
///   <item>Submitting a purchase is an inherently per-user action (the buyer's own receipt), so a non-user
///   (service-account) principal is denied 403 — it has no personal purchase to submit (the same rule as the
///   <c>/me</c> quota-status read and the Apple endpoint). The transaction is named globally by its (provider,
///   provider transaction id) pair and carries no tenant, so there is no organization/workspace boundary to
///   resolve here (CORE-STORE-002: <c>purchase_transactions</c> has no <c>organization_id</c>).</item>
///   <item>Only AFTER authorization is the request validated, so an unauthorized caller never receives
///   request-shape feedback: a missing body or a missing/blank/oversize purchase token is 400.</item>
///   <item>A genuine purchase is recorded and returned 200; a rejected token is 422 (a semantically invalid
///   command — the submitted token is not a grantable purchase) carrying only the generic, log-safe rejection
///   reason, with nothing recorded.</item>
/// </list>
///
/// Persistence dependency: like the Apple/asset/quota endpoints, this uses the
/// <see cref="PurchaseTransactionService"/> and <see cref="TimeProvider"/>, which are registered only when a
/// database connection string is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails
/// closed with 503. When no Google verification adapter is configured for the deployment, the fail-closed
/// resolver throws <see cref="PurchaseProviderNotConfiguredException"/> and the request is 503 (the verification
/// analogue of the unconfigured asset storage), so no premium state is ever granted without a real verification
/// behind it.
/// </summary>
internal static class GooglePurchaseEndpoints
{
    public static IEndpointRouteBuilder MapGooglePurchaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/purchases/google")
            .RequireAuthorization();

        group.MapPost("/tokens", VerifyGoogleTokenAsync);

        return endpoints;
    }

    // POST /api/v1/purchases/google/tokens
    private static async Task<IResult> VerifyGoogleTokenAsync(
        HttpContext httpContext,
        [FromBody] GoogleTokenVerificationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return ServiceUnavailable();
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return Unauthorized();
        }

        // Submitting a purchase is a per-user action: a service account has no personal purchase to verify, so it
        // is denied fail-closed (403) — the same rule as the /me quota-status read and the Apple endpoint. This is
        // the authorization gate for a route that has no tenant boundary (a purchase is named globally,
        // CORE-STORE-002).
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Authorized. Only now validate the request, so an unauthorized caller never receives request-shape
        // feedback. A missing/unparseable body cannot carry the token; 400. (Malformed JSON is rejected as 400 by
        // the framework before the handler.)
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.PurchaseToken))
        {
            return ValidationError("A purchase token is required.");
        }

        // Build the provider-neutral verification request for Google. The token is carried verbatim and is never
        // parsed, trusted or logged here. Create enforces the proof/product-reference bounds; an oversize value is
        // a 400 (never a server error, and never echoing the token).
        PurchaseVerificationRequest verificationRequest;
        try
        {
            verificationRequest = PurchaseVerificationRequest.Create(
                PurchaseProvider.Google,
                request.PurchaseToken,
                request.ProductReference);
        }
        catch (ArgumentException)
        {
            return ValidationError("The purchase token or product reference is invalid.");
        }

        // Resolve the deployment-supplied Google verifier. Fail-closed: when no adapter is configured the resolver
        // throws PurchaseProviderNotConfiguredException and the request is 503 — Core never trusts the unverified
        // token and grants nothing (the verification analogue of the unconfigured asset storage; threat T4/T7).
        IPurchaseVerificationProvider verifier;
        try
        {
            verifier = deps.Resolver.Resolve(PurchaseProvider.Google);
        }
        catch (PurchaseProviderNotConfiguredException)
        {
            return VerificationUnavailable();
        }

        // Verify the token against Google's server APIs. A definitive "not a genuine purchase" verdict is a
        // Rejected RESULT (handled below); a provider being unreachable/misconfigured is an EXCEPTION the adapter
        // throws, so a transient outage is never mistaken for a rejection — it surfaces as a 500 rather than a
        // false grant.
        var result = await verifier.VerifyAsync(verificationRequest, cancellationToken).ConfigureAwait(false);
        if (!result.IsVerified)
        {
            // Not a grantable purchase: record nothing, grant nothing. The generic, client-safe rejection reason
            // never echoes the token or any receipt content (threat T7).
            return VerificationRejected(result.RejectionReason);
        }

        // Verified. Persist the verified purchase as the recorded source of truth (idempotently — a retry or a
        // replayed-but-genuine token records no second row and no duplicate audit event; CORE-STORE-002). The
        // current persisted status is returned to the caller as confirmation.
        var now = deps.TimeProvider.GetUtcNow();
        var recording = await deps.Transactions
            .RecordVerifiedPurchaseAsync(result.Purchase!, now, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(PurchaseVerificationResponse.From(recording.Transaction));
    }

    /// <summary>
    /// Resolves the dependencies from the request scope. The verification provider resolver is always registered;
    /// the recording service and the clock exist only when a database connection string is configured, so when
    /// persistence is off the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out GooglePurchaseEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<PurchaseVerificationProviderResolver>();
        var transactions = services.GetService<PurchaseTransactionService>();
        var timeProvider = services.GetService<TimeProvider>();

        if (resolver is null
            || transactions is null
            || timeProvider is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new GooglePurchaseEndpointDependencies(resolver, transactions, timeProvider);
        return true;
    }

    private static bool TryMapPrincipal(HttpContext httpContext, out OidcPrincipal principal)
    {
        var result = OidcPrincipalMapper.Map(httpContext.User);
        if (!result.Succeeded)
        {
            principal = null!;
            return false;
        }

        principal = result.Principal;
        return true;
    }

    private static IResult ServiceUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Purchase verification requires persistence, which is not configured.");

    private static IResult VerificationUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Google purchase verification is not configured.");

    private static IResult Unauthorized()
        => Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // A well-formed submission whose token Google did not verify as a genuine purchase: a semantically invalid
    // command (docs/08_API_CONTRACTS.md 422). The detail is the adapter's generic, client-safe and log-safe reason
    // (never the token or receipt content; threat T7); a missing reason falls back to a generic phrase.
    private static IResult VerificationRejected(string? reason)
        => Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Unprocessable Entity",
            detail: string.IsNullOrWhiteSpace(reason) ? "The purchase token could not be verified." : reason);

    private readonly record struct GooglePurchaseEndpointDependencies(
        PurchaseVerificationProviderResolver Resolver,
        PurchaseTransactionService Transactions,
        TimeProvider TimeProvider);
}
