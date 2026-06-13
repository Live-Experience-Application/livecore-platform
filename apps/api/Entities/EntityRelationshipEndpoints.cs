using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entities;

/// <summary>
/// HTTP endpoint of the Entities module's relationship REMOVAL (CORE-LIFE-002, the "Resource
/// Lifecycle and Deletion" epic). Until this story an <see cref="EntityRelationship"/> edge could be
/// ADDED but never removed — the graph only ever grew — so this adds the missing inverse: a host can
/// remove a directed entity relationship edge.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}</c> —
///   module Entities, roles "Host,CoHost,Owner,Admin". Removes ONE directed edge, addressed within
///   its parent workspace. Returns <c>204 No Content</c> on success.</item>
/// </list>
///
/// WORKSPACE-SCOPED LOOKUP (the story's headline requirement): the relationship's endpoint foreign
/// keys do NOT DB-enforce that the edge, its source and its target all live in the same workspace
/// (mirrors <see cref="Entity.EntityTypeId"/> / <c>ContentBlock.SceneId</c>), so the parent workspace
/// is resolved FIRST and the edge is then looked up THROUGH it: the route pins the
/// <c>{workspaceId}</c> in the path (exactly like the member-removal route), the target organization
/// is supplied by a required <c>?organizationSlug=</c> query parameter, and the edge is loaded with
/// <see cref="IEntityRelationshipRepository.FindByIdAsync"/>, which scopes the lookup by
/// <c>organization_id</c> THEN <c>workspace_id</c> THEN the edge id. An edge that lives in another
/// workspace, or in a workspace owned by another tenant, is therefore never reachable to remove even
/// when its surrogate id is known (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// load-then-authorize, fail-closed at every step and never leaking why — the same shape the scene /
/// content-block write routes use, since an entity relationship is host-prepared workspace content:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a
///   failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id or relationship
///   id, and a denied tenant resolution, are hidden as 404 (a caller who cannot see the tenant must
///   not learn whether the edge exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a non-member must
///   not learn the workspace's edges exist; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the remove role is 403. The remove role set is
///   <c>Owner</c>/<c>Admin</c>/<c>Host</c>/<c>CoHost</c> (csv/api_routes.csv;
///   docs/06_AUTHORIZATION_MATRIX.md — entity relationships are host-prepared content, governed by
///   the same host-capable roles as scenes and content blocks). <see cref="MembershipRole"/> is
///   non-linear, so the check is EXACT set membership, never an ordering comparison.</item>
///   <item>Once authorized, a relationship id that addresses no edge within the resolved workspace
///   (an unknown id, or one belonging to another workspace/tenant) is a SAFE 404 — removing a
///   non-existent edge reveals nothing and changes nothing (the story acceptance criterion).</item>
/// </list>
///
/// This story removes a recorded fact only; it adds no event and no audit record (the edge model
/// carries no visibility surface and the threat model lists no audit control for relationship
/// removal), staying faithful to the CORE-ENT-003 precedent where adding an edge emitted neither.
///
/// Persistence dependency: like the scene endpoints, this uses the repositories and the tenant
/// context resolver, which are registered only when a database connection string is configured (see
/// <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than
/// crashing startup.
/// </summary>
internal static class EntityRelationshipEndpoints
{
    /// <summary>Required value naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapEntityRelationshipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid
        // token is challenged as 401 before any handler runs.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapDelete(
            "/{workspaceId}/entity-relationships/{relationshipId}",
            RemoveEntityRelationshipAsync);

        return endpoints;
    }

    // DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}?organizationSlug={slug}
    private static async Task<IResult> RemoveEntityRelationshipAsync(
        HttpContext httpContext,
        string workspaceId,
        string relationshipId,
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

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the member-removal and by-session-id
        // routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or relationship id can never address a stored row; treat each as
        // hidden (404), never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenRelationship();
        }

        if (!Guid.TryParse(relationshipId, out var relationshipGuid) || relationshipGuid == Guid.Empty)
        {
            return HiddenRelationship();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing edge (threat T5).
            return HiddenRelationship();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace (the parent
        // workspace, resolved first). A caller who is a member of the tenant but NOT of the workspace
        // must not learn the workspace's edges exist, so a missing membership is hidden as 404 (not
        // 403) — the same rule as the scene/content write routes (threats T1/T5). The member's
        // workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenRelationship();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized
        // to know the workspace exists, but not to remove its relationships). Entity relationships are
        // host-prepared content, governed by the host-capable roles Owner/Admin/Host/CoHost
        // (csv/api_routes.csv; docs/06_AUTHORIZATION_MATRIX.md), the same set that creates scenes and
        // content blocks. MembershipRole is non-linear, so this is an EXACT set membership check,
        // never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Load the edge WITHIN the resolved tenant AND workspace. FindByIdAsync leads with
        // organization_id then workspace_id then the edge id, so an edge in another workspace or
        // tenant is never returned even when the surrogate id matches; an unknown id is simply null.
        // Removing a non-existent edge is a SAFE 404 (the story acceptance criterion): it reveals
        // nothing and changes nothing.
        var relationship = await deps.EntityRelationships
            .FindByIdAsync(context.OrganizationId, workspaceGuid, relationshipGuid, cancellationToken)
            .ConfigureAwait(false);
        if (relationship is null)
        {
            return HiddenRelationship();
        }

        // Hard-delete exactly this one edge. Only the relationship row is removed; the two endpoint
        // entities are untouched.
        await deps.EntityRelationships.RemoveAsync(relationship, cancellationToken).ConfigureAwait(false);

        // The edge is gone; nothing is returned (204 No Content).
        return Results.NoContent();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a
    /// database connection string is configured; when absent, the endpoint fails closed with 503
    /// instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out EntityRelationshipEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var entityRelationships = services.GetService<IEntityRelationshipRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || entityRelationships is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new EntityRelationshipEndpointDependencies(resolver, entityRelationships, workspaceMembers);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an <see cref="OidcPrincipal"/>,
    /// fail-closed: a failed mapping yields <see langword="false"/> (the caller returns 401). The
    /// mapping error reason is never echoed to the response (threat T7).
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
            detail: "Entity relationship operations require persistence, which is not configured.");

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

    private static IResult MissingOrganization()
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    // Relationship existence is hidden: a malformed workspace/relationship id, an edge in a foreign or
    // non-entitled tenant, an edge in another workspace, an unknown edge, and an edge in a workspace
    // the caller does not belong to are ALL reported as 404, never distinguishable from each other and
    // never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenRelationship()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct EntityRelationshipEndpointDependencies(
        TenantContextResolver Resolver,
        IEntityRelationshipRepository EntityRelationships,
        IWorkspaceMemberRepository WorkspaceMembers);
}
