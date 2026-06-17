// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// HTTP endpoint of the Entitlements module's current-user effective-entitlements read
/// (CORE-API-007, the documented-not-built <c>GET /v1/me/entitlements</c> of
/// csv/mobile_store_api_routes.csv). It realizes the documented request flow (authentication →
/// endpoint → authorization → server-side resolution, docs/02_ARCHITECTURE.md) for the single
/// entitlements read, mirroring the sibling <c>GET /api/v1/me/ad-eligibility</c> and
/// <c>GET /api/v1/me/quota-status</c> endpoints exactly.
///
/// Route owned by this story (csv/mobile_store_api_routes.csv <c>GET /v1/me/entitlements</c>,
/// surfaced under the Core <c>/api/v1</c> prefix that docs/08_API_CONTRACTS.md mandates for all
/// APIs — the same prefix the quota-status and ad-eligibility routes use; added to
/// csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/me/entitlements</c> — the CURRENT USER's resolved, server-authoritative
///   effective entitlements (the premium state). Authorized to any authenticated user principal
///   (their own entitlements); a service account has no personal premium state and is 403.</item>
/// </list>
///
/// THE EPIC ACCEPTANCE CRITERION — "User-visible premium state comes only from server entitlements"
/// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The response is produced ENTIRELY by the
/// CORE-ENTL-002 <see cref="SubjectEntitlementResolver"/> (the single server-side path that resolves
/// a subject's active assignments into its effective entitlements), reused unchanged — there is no
/// client input and no parallel resolution path, so the REST read can never diverge from an internal
/// feature guard. A subject with no active assignment is entitled to nothing (the fail-closed
/// default).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>The bearer middleware challenges a missing/invalid token with 401 before any handler
///   runs.</item>
///   <item>The authenticated principal is mapped fail-closed from the request's claims; a failed
///   mapping is 401.</item>
///   <item><c>/me/entitlements</c> is a USER concept, so a non-user (service-account) principal is
///   403 — it has no personal premium state (the same rule as <c>/me/ad-eligibility</c>,
///   <c>/me/quota-status</c> and the purchase endpoints).</item>
///   <item>It resolves the current user's profile through <see cref="UserProfileReferenceService"/>
///   (the canonical "current user" resolution, idempotent on first sight) and resolves the USER
///   subject keyed by the profile id. The resolver reads ONLY that subject's assignments, so one
///   user's premium state is never returned through another's id (per-subject isolation, threat T5);
///   the response carries no subject id.</item>
/// </list>
///
/// Persistence dependency: like the quota-status/ad-eligibility endpoints, this uses the entitlement
/// resolver and the user profile service, which are registered only when a database connection string
/// is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class MeEntitlementsEndpoints
{
    public static IEndpointRouteBuilder MapMeEntitlementsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid
        // token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapGet("/me/entitlements", GetMyEntitlementsAsync);

        return endpoints;
    }

    // GET /api/v1/me/entitlements
    private static async Task<IResult> GetMyEntitlementsAsync(
        HttpContext httpContext,
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

        // /me is inherently a user concept: a service account has no personal premium state, so it is
        // denied fail-closed (403) rather than provisioning a user profile for a machine client.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user" resolution; idempotent on
        // first sight), then resolve the USER subject's effective entitlements server-side. The
        // subject id is the profile id; the response carries only the generic entitlement facts, never
        // the subject id.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var entitlements = await deps.Resolver
            .ResolveAsync(EntitlementSubjectType.User, profile.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(MeEntitlementsResponse.From(entitlements));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a
    /// database connection string is configured; when absent, the endpoint fails closed with 503
    /// instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out MeEntitlementsEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<SubjectEntitlementResolver>();
        var userProfiles = services.GetService<UserProfileReferenceService>();

        if (resolver is null || userProfiles is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new MeEntitlementsEndpointDependencies(resolver, userProfiles);
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Entitlement operations require persistence, which is not configured.");

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult Forbidden()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status403Forbidden,
            code: ProblemCodes.PermissionDenied,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    private readonly record struct MeEntitlementsEndpointDependencies(
        SubjectEntitlementResolver Resolver,
        UserProfileReferenceService UserProfiles);
}
