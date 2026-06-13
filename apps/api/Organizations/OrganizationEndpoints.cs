using LiveCore.Api.Audit;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Organizations;

/// <summary>
/// HTTP endpoints of the Organizations module (CORE-API-001: organization
/// create and read API). The Organizations module already owned the
/// <see cref="Organization"/> aggregate, its repositories and the tenant
/// context resolver, but no endpoint was mapped; this story surfaces the two
/// routes csv/api_routes.csv already defines, realizing the documented request
/// flow "authentication middleware -> tenant context -> endpoint ->
/// authorization" (docs/02_ARCHITECTURE.md) and following the
/// <c>WorkspaceEndpoints.cs</c> pattern.
///
/// Routes owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET  /api/v1/organizations</c> — list the organizations the
///   caller belongs to ("Only organizations user belongs to"). The result is
///   the intersection of the caller's persisted memberships and the token's
///   organization claims, so it never lists a tenant the caller is not a
///   member of, nor one the token does not assert.</item>
///   <item><c>POST /api/v1/organizations</c> — create a new tenant and make the
///   caller its founding <c>Owner</c> ("Creates organization and Owner
///   membership"), atomically.</item>
///   <item><c>DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}</c>
///   — remove an organization member (CORE-LIFE-001), "Manage members" (Owner or
///   Admin). Hard-deletes the membership, revoking the subject's tenant access on
///   their next request; the sole tenant Owner cannot be removed (the last-Owner
///   invariant, 409 — an ownerless tenant would be permanently unreachable); the
///   removal is appended to the append-only audit log
///   (<see cref="AuditAction.MemberRemoved"/>). Fail-closed and hidden as 404 for a
///   cross-tenant/unknown organization or member.</item>
/// </list>
///
/// Authorization model (server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5):
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's
///   claims to an <see cref="OidcPrincipal"/>; a failed mapping is 401.</item>
///   <item>Both routes are user-tenant operations: only a human user holds an
///   organization membership (an <see cref="OrganizationMember"/> references a
///   user profile), so a service-account principal is denied 403 — exactly as
///   the tenant context resolver denies a non-user (<c>NotAUser</c>) and the
///   <c>/me</c> routes deny a service account. This is the unauthorized-role
///   denial for these routes.</item>
///   <item>The tenant boundary is the token's organization claim, matched
///   exactly (<see cref="OidcPrincipal.HasOrganizationClaim"/>) — the same
///   token-asserted boundary <see cref="TenantContextResolver"/> enforces. On
///   create, a slug the token does not claim is a FOREIGN tenant and is hidden
///   as 404 (fail-closed; the create never reveals whether that tenant exists).
///   On list, an organization the token does not claim is simply not returned.
///   </item>
///   <item>Creating a tenant that already exists (a taken slug) is a 409 and
///   adds NO membership, so a caller can never escalate a create into ownership
///   of a pre-existing organization (threats T5/T1).</item>
/// </list>
///
/// The tenant context resolver is not invoked by the create or list routes: it
/// resolves an EXISTING organization the caller is already a member of, which
/// neither the create (the tenant does not exist yet) nor the cross-tenant list
/// (many organizations, not one) is. Instead those two routes reuse the
/// resolver's building blocks — the same OIDC principal mapping, the same exact
/// organization-claim check, the same repositories and membership model — so the
/// per-organization decision is identical to what the resolver would grant. The
/// member removal route, by contrast, acts on one EXISTING organization the
/// caller belongs to, so it DOES use <see cref="TenantContextResolver"/>
/// directly (token claim AND persisted membership), exactly like the
/// workspace-scoped routes.
///
/// Persistence dependency: the endpoints use the organization repository and the
/// user-profile reference service, which are registered only when a database
/// connection string is configured (see <c>Program.cs</c>). When persistence is
/// off the endpoints fail closed with 503, keeping the smoke tests green.
/// </summary>
internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/organizations")
            .RequireAuthorization();

        group.MapGet("/", ListOrganizationsAsync);
        group.MapPost("/", CreateOrganizationAsync);
        group.MapDelete("/{organizationSlug}/members/{memberId}", RemoveOrganizationMemberAsync);

        return endpoints;
    }

    // GET /api/v1/organizations
    private static async Task<IResult> ListOrganizationsAsync(
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

        // "Organizations the user belongs to" is inherently a user concept: a
        // service account holds no user profile and no organization membership,
        // so it is denied fail-closed (403), exactly as the tenant resolver
        // denies a non-user (NotAUser) and the /me routes deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Resolve the current user's profile (the canonical "current user"
        // resolution; idempotent on first sight), then list the organizations the
        // subject is a member of.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var organizations = await deps.Organizations
            .ListByMemberAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);

        // Defence in depth (threat T5): return only an organization the token
        // ALSO asserts as a claim, so the listing can never diverge from what the
        // tenant resolver would grant per organization (claim AND membership). A
        // persisted membership without a matching token claim — a foreign tenant
        // from the token's point of view — is never listed.
        var response = organizations
            .Where(organization => principal.HasOrganizationClaim(organization.Slug))
            .Select(OrganizationResponse.From)
            .ToArray();

        return Results.Ok(response);
    }

    // POST /api/v1/organizations
    private static async Task<IResult> CreateOrganizationAsync(
        HttpContext httpContext,
        [FromBody] CreateOrganizationRequest? request,
        TimeProvider timeProvider,
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

        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // Validate the inputs before any authorization/tenant work, so a broken
        // body is a 400 regardless of who is calling (mirrors WorkspaceEndpoints).
        if (!Organization.IsValidName(request.Name?.Trim()))
        {
            return ValidationError("A valid organization name is required.");
        }

        string canonicalSlug;
        try
        {
            canonicalSlug = Organization.CanonicalizeSlug(request.Slug);
        }
        catch (ArgumentException)
        {
            return ValidationError("A valid organization slug is required.");
        }

        // Only a human user can own a tenant: the founding membership is a user
        // membership (OrganizationMember references a user profile). A service
        // account is denied fail-closed (403), exactly as the tenant resolver
        // denies a non-user and the /me routes deny a service account.
        if (principal.Type != PrincipalType.User)
        {
            return Forbidden();
        }

        // Tenant boundary (threat T5): the token must ASSERT this tenant. A caller
        // can only create the organization their identity provider scoped the
        // token to; creating a slug the token does not claim is a FOREIGN tenant
        // and is hidden as 404 (fail-closed; the create never reveals whether the
        // tenant already exists). This is the same exact organization-claim check
        // the tenant context resolver performs.
        if (!principal.HasOrganizationClaim(canonicalSlug))
        {
            return HiddenOrganization();
        }

        // Provision the caller's user profile (idempotent on first sight; the
        // canonical "current user" resolution), so the founding owner membership
        // can reference it.
        var profile = await deps.UserProfiles
            .EnsureUserProfileAsync(principal, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var organization = Organization.Create(canonicalSlug, request.Name!.Trim(), now);
        var owner = OrganizationMember.Create(organization.Id, profile.Id, MembershipRole.Owner, now);

        // Create the tenant and its founding Owner membership ATOMICALLY: a tenant
        // is never left without an owner, and a duplicate slug rolls the
        // membership back too (no privilege escalation into a pre-existing tenant).
        var addResult = await deps.Organizations
            .AddWithOwnerAsync(organization, owner, cancellationToken)
            .ConfigureAwait(false);
        if (addResult == OrganizationAddResult.DuplicateSlug)
        {
            // The slug is the globally-unique natural key, so a second create is a
            // 409 Conflict (docs/08_API_CONTRACTS.md). The error carries no other
            // tenant data, and no membership was created.
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: "An organization with this slug already exists.");
        }

        var response = OrganizationResponse.From(organization);
        return Results.Created($"/api/v1/organizations/{organization.Id}", response);
    }

    // DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}
    private static async Task<IResult> RemoveOrganizationMemberAsync(
        HttpContext httpContext,
        string organizationSlug,
        string memberId,
        TimeProvider timeProvider,
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

        // The organization is identified by its slug in the path (the tenant's natural key, matched against
        // the token's organization claim). A missing/blank slug or a malformed member id can never address a
        // stored row; hide as 404, never echoing why.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return HiddenOrganization();
        }

        if (!Guid.TryParse(memberId, out var memberGuid) || memberGuid == Guid.Empty)
        {
            return HiddenOrganization();
        }

        // Resolve the trusted tenant context (token claim AND persisted membership). A denied resolution —
        // a foreign/unknown tenant, a malformed slug, a non-member or a service-account principal — is
        // hidden as 404, so a tenant the caller cannot see is indistinguishable from a missing one (threat
        // T5). The resolver canonicalizes the slug and never throws on a malformed one.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md). The caller is a known
        // member of the tenant (the resolution proved it), so an insufficient role is a 403. Exact,
        // non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Find the target membership WITHIN this tenant; a member id that belongs to another organization
        // (or no membership at all) is hidden as 404, never 403, so a member outside the caller's resolved
        // tenant can never be probed for (threats T1/T5).
        var target = await deps.OrganizationMembers
            .FindByIdAsync(context.OrganizationId, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return HiddenOrganization();
        }

        // Last-Owner invariant: the sole Owner of the tenant cannot be removed — an ownerless organization
        // would be permanently unreachable, since the tenant resolver requires a membership. Only relevant
        // when the target itself is an Owner; the count is tenant-scoped. This is an invariant conflict
        // (409), not an authorization failure.
        if (target.HasRole(MembershipRole.Owner))
        {
            var ownerCount = await deps.OrganizationMembers
                .CountByRoleAsync(context.OrganizationId, MembershipRole.Owner, cancellationToken)
                .ConfigureAwait(false);
            if (ownerCount <= 1)
            {
                return LastOwnerConflict();
            }
        }

        // Capture the removed identity/role BEFORE deletion for the audit record.
        var removedMemberId = target.Id;
        var removedRole = target.Role;

        await deps.OrganizationMembers.RemoveAsync(target, cancellationToken).ConfigureAwait(false);

        // AUDIT: removal is security-relevant access revocation, so append an append-only audit record
        // capturing the actor (the admin who removed the member), the removed membership and the revoked
        // role (threats T1/T6). An organization member removal is organization-level, so it records no
        // workspace. The audit row outlives the now-deleted membership (recorded fact, not FK).
        var now = timeProvider.GetUtcNow();
        var entry = AuditLogEntry.ForMemberRemoval(
            context.OrganizationId,
            workspaceId: null,
            context.UserProfileId,
            nameof(OrganizationMember),
            removedMemberId,
            removedRole.ToString(),
            now);
        await deps.AuditLog.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // The membership row IS the access grant, so its deletion has already revoked the subject's tenant
        // access; nothing is returned (204 No Content).
        return Results.NoContent();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent,
    /// the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out OrganizationEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var organizations = services.GetService<IOrganizationRepository>();
        var userProfiles = services.GetService<UserProfileReferenceService>();
        var resolver = services.GetService<TenantContextResolver>();
        var organizationMembers = services.GetService<IOrganizationMemberRepository>();
        var auditLog = services.GetService<IAuditLogRepository>();

        if (organizations is null
            || userProfiles is null
            || resolver is null
            || organizationMembers is null
            || auditLog is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new OrganizationEndpointDependencies(
            organizations, userProfiles, resolver, organizationMembers, auditLog);
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
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Organization operations require persistence, which is not configured.");

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

    // The last Owner of an organization cannot be removed: an ownerless tenant
    // would be permanently unreachable (the tenant resolver requires a
    // membership). The caller is authorized; the invariant, not the caller, is
    // the reason, so this is a 409 Conflict (docs/08_API_CONTRACTS.md). The
    // detail names only the generic invariant and leaks no tenant data (threat T7).
    private static IResult LastOwnerConflict()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The last Owner of the organization cannot be removed.");

    // Tenant existence is hidden: a foreign tenant (a slug the token does not
    // claim) is reported as 404, never echoing the reason (docs/08; threat T5).
    private static IResult HiddenOrganization()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct OrganizationEndpointDependencies(
        IOrganizationRepository Organizations,
        UserProfileReferenceService UserProfiles,
        TenantContextResolver Resolver,
        IOrganizationMemberRepository OrganizationMembers,
        IAuditLogRepository AuditLog);
}
