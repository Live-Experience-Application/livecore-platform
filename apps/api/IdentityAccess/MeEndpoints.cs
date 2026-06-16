using LiveCore.Api.Organizations;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// HTTP endpoint of the IdentityAccess module's current-principal surface
/// (CORE-API-002: implement <c>GET /api/v1/me</c>). The IdentityAccess module
/// already owned authenticated principal mapping (<see cref="OidcPrincipalMapper"/>)
/// and the user profile reference (<see cref="UserProfileReferenceService"/>), but
/// no endpoint projected them; this story surfaces the route
/// csv/api_routes.csv already defines ("Returns principal context only"),
/// realizing the documented request flow "authentication middleware -> principal
/// mapping -> endpoint" (docs/02_ARCHITECTURE.md) and following the
/// <c>OrganizationEndpoints.cs</c> / <c>QuotaStatusEndpoints.cs</c> pattern.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/me</c> — the authenticated caller's principal context:
///   their own user profile plus the organization memberships they hold and the
///   role in each.</item>
/// </list>
///
/// Authorization model (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5/T7):
/// <list type="bullet">
///   <item>The route group requires authorization, so an anonymous caller is
///   challenged with 401 before any handler runs (the story's "anonymous callers
///   get 401" criterion).</item>
///   <item>The authenticated principal is mapped fail-closed from the request's
///   claims; a failed mapping is 401, and the mapping reason is never echoed
///   (threat T7).</item>
///   <item><c>/me</c> is inherently a user concept: only a human user holds a user
///   profile and organization memberships. A service-account principal has
///   neither, so it is denied 403 — exactly as the sibling <c>/me/quota-status</c>
///   and <c>/me/ad-eligibility</c> routes and the organization routes deny a
///   service account. This is the unauthorized-role denial for this route.</item>
///   <item>The membership list is the INTERSECTION of the caller's persisted
///   memberships and the token's organization claims
///   (<see cref="OidcPrincipal.HasOrganizationClaim"/>) — the same token-asserted
///   boundary <see cref="TenantContextResolver"/> enforces and the
///   <c>GET /api/v1/organizations</c> listing applies. A persisted membership the
///   token does not assert — a foreign tenant from the token's point of view — is
///   never returned, so the principal context never exposes another tenant
///   (threat T5; the story note "do not expose other tenants").</item>
/// </list>
///
/// The response is a safe DTO of identifiers and the caller's own display
/// metadata only — never the access token, the raw organization claim values or
/// an authorization rationale (threat T7; the story note "No secrets/tokens in the
/// response").
///
/// Persistence dependency: like the organization/quota endpoints, the handler uses
/// the user-profile reference service and the organization repository, which are
/// registered only when a database connection string is configured (see
/// <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/me")
            .RequireAuthorization();

        group.MapGet("/", GetCurrentPrincipalAsync);

        return endpoints;
    }

    // GET /api/v1/me
    private static async Task<IResult> GetCurrentPrincipalAsync(
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

        // /me is inherently a user concept: a service account holds no user
        // profile and no organization membership, so it is denied fail-closed
        // (403), exactly as the tenant resolver denies a non-user (NotAUser) and
        // the sibling /me/quota-status and /me/ad-eligibility routes deny a service
        // account, rather than provisioning a profile for a machine client.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user"
        // resolution; idempotent on first sight, mirroring changed display
        // metadata), then list the memberships and roles for the subject.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var memberships = await deps.Organizations
            .ListMembershipsByMemberAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);

        // Defence in depth (threat T5): include only a membership whose
        // organization the token ALSO asserts as a claim, so the principal context
        // can never diverge from what the tenant resolver would grant per
        // organization (claim AND membership). A persisted membership without a
        // matching token claim — a foreign tenant from the token's point of view —
        // is never exposed.
        var visibleMemberships = memberships
            .Where(membership => principal.HasOrganizationClaim(membership.Organization.Slug))
            .Select(OrganizationMembershipResponse.From)
            .ToArray();

        return Results.Ok(CurrentPrincipalResponse.From(profile, visibleMemberships));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent,
    /// the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out MeEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var userProfiles = services.GetService<UserProfileReferenceService>();
        var organizations = services.GetService<IOrganizationRepository>();

        if (userProfiles is null || organizations is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new MeEndpointDependencies(userProfiles, organizations);
        return true;
    }

    /// <summary>
    /// Maps the authenticated principal to an <see cref="OidcPrincipal"/>,
    /// fail-closed: a failed mapping yields <see langword="false"/> (the caller
    /// returns 401). The mapping error reason is never echoed (threat T7).
    /// </summary>
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
            detail: "Principal context requires persistence, which is not configured.");

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

    private readonly record struct MeEndpointDependencies(
        UserProfileReferenceService UserProfiles,
        IOrganizationRepository Organizations);
}
