// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// HTTP endpoint of the Ad Eligibility epic (CORE-ADS-001). It realizes the documented request flow
/// (authentication → endpoint → authorization → server-side decision, docs/02_ARCHITECTURE.md) for the single
/// ad-eligibility read, mirroring the <c>GET /api/v1/me/quota-status</c> endpoint.
///
/// Route owned by this story (csv/mobile_store_api_routes.csv <c>GET /v1/me/ad-eligibility</c>, surfaced under the
/// Core <c>/api/v1</c> prefix that docs/08_API_CONTRACTS.md mandates; added to csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/me/ad-eligibility</c> — whether the CURRENT USER must be shown ads, decided entirely from
///   the user's server entitlements. Authorized to any authenticated user principal (their own eligibility); a
///   service account has no personal premium state and is 403.</item>
/// </list>
///
/// THE EPIC ACCEPTANCE CRITERION — "Core returns ad eligibility without knowing ad placements". The response carries
/// only the generic, entitlement-derived decision (<see cref="AdEligibilityResponse"/>); Core never renders,
/// requests, configures or places ads, and the endpoint includes no ad provider/unit config
/// (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>The bearer middleware challenges a missing/invalid token with 401 before any handler runs.</item>
///   <item>The authenticated principal is mapped fail-closed from the request's claims; a failed mapping is 401.</item>
///   <item><c>/me/ad-eligibility</c> is a USER concept, so a non-user (service-account) principal is 403 — it has no
///   personal premium state (the same rule as <c>/me/quota-status</c> and the purchase endpoints).</item>
///   <item>It resolves the current user's profile through <see cref="UserProfileReferenceService"/> (the canonical
///   "current user" resolution, idempotent on first sight) and decides for the USER subject, keyed by the profile
///   id. The decision reads ONLY that subject's entitlements, so one user's premium state is never returned through
///   another's id (per-subject isolation, threat T5); the response carries no subject id.</item>
/// </list>
///
/// Persistence dependency: like the quota-status endpoint, this uses the entitlement resolver (via
/// <see cref="AdEligibilityService"/>) and the user profile service, which are registered only when a database
/// connection string is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with
/// 503.
/// </summary>
internal static class AdEligibilityEndpoints
{
    public static IEndpointRouteBuilder MapAdEligibilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapGet("/me/ad-eligibility", GetMyAdEligibilityAsync);

        return endpoints;
    }

    // GET /api/v1/me/ad-eligibility
    private static async Task<IResult> GetMyAdEligibilityAsync(
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

        // /me is inherently a user concept: a service account has no personal premium state, so it is denied
        // fail-closed (403) rather than provisioning a user profile for a machine client.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user" resolution; idempotent on first sight),
        // then decide the USER subject's ad eligibility server-side. The subject id is the profile id; the response
        // carries only the generic ad-eligibility facts, never the subject id.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var result = await deps.AdEligibility
            .EvaluateForSubjectAsync(EntitlementSubjectType.User, profile.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(AdEligibilityResponse.From(result));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out AdEligibilityEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var adEligibility = services.GetService<AdEligibilityService>();
        var userProfiles = services.GetService<UserProfileReferenceService>();

        if (adEligibility is null || userProfiles is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new AdEligibilityEndpointDependencies(adEligibility, userProfiles);
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
            detail: "Ad eligibility requires persistence, which is not configured.");

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

    private readonly record struct AdEligibilityEndpointDependencies(
        AdEligibilityService AdEligibility,
        UserProfileReferenceService UserProfiles);
}
