using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// HTTP endpoints of the Entitlements module's quota-status surface (CORE-ENTL-003, the quota definition and quota
/// status story of the "Entitlements and Quotas" epic). They realize the documented request flow
/// (authentication → tenant/workspace context resolver → endpoint → authorization → calculation,
/// docs/02_ARCHITECTURE.md) for the two generic quota-status reads, mirroring the workspace/asset endpoints.
///
/// Routes owned by this story (csv/mobile_store_api_routes.csv, surfaced under the Core <c>/api/v1</c> prefix that
/// docs/08_API_CONTRACTS.md mandates for all APIs; added to csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/me/quota-status</c> — the CURRENT USER's server-calculated quota status. Authorized to
///   any authenticated user principal (their own status); a service account has no personal quota and is 403.</item>
///   <item><c>GET /api/v1/workspaces/{workspaceId}/quota-status</c> — a WORKSPACE's server-calculated quota
///   status. "Must enforce membership authorization" (csv): the caller must be a host-capable member of the
///   workspace.</item>
/// </list>
///
/// THE EPIC ACCEPTANCE CRITERION — "Quota status is calculated server-side for subjects and workspaces". Both
/// routes compute the status entirely server-side through <see cref="QuotaStatusCalculator"/> (catalog limits +
/// recorded usage); a client supplies no part of it, and the fail-closed calculation never reports headroom the
/// server did not grant (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5):
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's claims; a failed mapping is
///   401.</item>
///   <item><c>/me/quota-status</c> resolves the current user's profile through
///   <see cref="UserProfileReferenceService"/> (the canonical "current user" resolution, idempotent on first
///   sight) and calculates for the USER subject. A non-user (service-account) principal is 403 — it has no
///   personal quota.</item>
///   <item><c>/workspaces/{workspaceId}/quota-status</c> resolves the target organization from a required
///   <c>?organizationSlug=</c> query parameter via <see cref="TenantContextResolver"/> (token claim AND persisted
///   membership), then requires the caller to be a member of THAT workspace. A caller who cannot see the tenant,
///   and a caller who is not a member of the workspace, are hidden as 404 (never distinguishable, never 403 for a
///   non-member). A known member who is not a host-capable role (Owner/Admin/Host/CoHost — the workspace
///   management roles; <see cref="MembershipRole"/> is non-linear, so the check is EXACT) is 403.</item>
/// </list>
///
/// Persistence dependency: like the workspace/asset endpoints, these use the quota repositories, the entitlement
/// resolver, the tenant context resolver, the workspace member repository and the user profile service, which are
/// registered only when a database connection string is configured (see <c>Program.cs</c>); when persistence is
/// off the endpoints fail closed with 503.
/// </summary>
internal static class QuotaStatusEndpoints
{
    /// <summary>Required query parameter naming the target organization on the workspace quota-status route.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapQuotaStatusEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1")
            .RequireAuthorization();

        group.MapGet("/me/quota-status", GetMyQuotaStatusAsync);
        group.MapGet("/workspaces/{workspaceId}/quota-status", GetWorkspaceQuotaStatusAsync);

        return endpoints;
    }

    // GET /api/v1/me/quota-status
    private static async Task<IResult> GetMyQuotaStatusAsync(
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

        // /me is inherently a user concept: a service account has no personal premium state or quota, so it is
        // denied fail-closed (403) rather than provisioning a user profile for a machine client.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user" resolution; idempotent on first sight),
        // then calculate the USER subject's quota status server-side. The subject id is the profile id; the
        // response carries only the generic quota facts, never the subject id.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var status = await deps.Calculator
            .CalculateForSubjectAsync(EntitlementSubjectType.User, profile.Id, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(QuotaStatusResponse.From(status));
    }

    // GET /api/v1/workspaces/{workspaceId}/quota-status?organizationSlug={slug}
    private static async Task<IResult> GetWorkspaceQuotaStatusAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
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

        // The target organization is required to resolve the tenant; the route path carries only the workspace id,
        // so it is a query parameter exactly like the workspace read-one route.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty workspace id can never address a stored workspace; hidden as 404, never echoing why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A caller who is a member of
        // the tenant but NOT of the workspace must not learn the workspace exists, so a missing membership is
        // hidden as 404 (not 403) — the same rule as the workspace read-one route (threats T1/T5). The member's
        // workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // A workspace's quota status (its limits and usage) is workspace management metadata, so it is authorized
        // to the host-capable management roles (Owner/Admin/Host/CoHost — the "View host-only content" /
        // workspace-management capability of docs/06_AUTHORIZATION_MATRIX.md, the same set as scene/asset writes).
        // The audience roles (Participant/Observer) and the audit role are denied fail-closed (403). MembershipRole
        // is non-linear, so this is an EXACT set membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Calculate the WORKSPACE subject's quota status server-side. The subject id is the workspace id.
        var status = await deps.Calculator
            .CalculateForSubjectAsync(EntitlementSubjectType.Workspace, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(QuotaStatusResponse.From(status));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out QuotaStatusEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var calculator = services.GetService<QuotaStatusCalculator>();
        var userProfiles = services.GetService<UserProfileReferenceService>();
        var resolver = services.GetService<TenantContextResolver>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (calculator is null
            || userProfiles is null
            || resolver is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new QuotaStatusEndpointDependencies(calculator, userProfiles, resolver, workspaceMembers);
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
            detail: "Quota operations require persistence, which is not configured.");

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

    private static IResult MissingOrganization()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    // Workspace existence is hidden: a malformed workspace id, a workspace in a foreign or non-entitled tenant,
    // and a workspace the caller does not belong to are ALL reported as 404, never distinguishable and never
    // echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenWorkspace()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct QuotaStatusEndpointDependencies(
        QuotaStatusCalculator Calculator,
        UserProfileReferenceService UserProfiles,
        TenantContextResolver Resolver,
        IWorkspaceMemberRepository WorkspaceMembers);
}
