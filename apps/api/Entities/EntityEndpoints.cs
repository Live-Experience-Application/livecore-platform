using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entities;

/// <summary>
/// HTTP endpoint of the Entities module's entity DELETION (CORE-LIFE-003, the "Resource Lifecycle and
/// Deletion" epic). A host can delete an <see cref="Entity"/>; its dependent relationship edges,
/// visibility rules and asset links are CASCADED (cleaned up) consistently, the deletion is authorized
/// server-side and it is appended to the append-only audit log.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}</c> — module Entities, roles
///   "Host,CoHost,Owner,Admin". Deletes ONE entity, addressed within its parent workspace, cascading its
///   dependents. Returns <c>204 No Content</c> on success.</item>
/// </list>
///
/// WORKSPACE-SCOPED LOOKUP (the story's headline requirement, mirroring the entity-relationship removal
/// route CORE-LIFE-002): the parent workspace is resolved FIRST and the entity is then loaded THROUGH it.
/// The route pins the <c>{workspaceId}</c> in the path, the target organization is supplied by a required
/// <c>?organizationSlug=</c> query parameter, and the entity is loaded (by
/// <see cref="EntityDeletionService"/>) with <see cref="IEntityRepository.FindByIdAsync"/>, which scopes
/// the lookup by <c>organization_id</c> THEN <c>workspace_id</c> THEN the entity id. An entity that lives
/// in another workspace, or in a workspace owned by another tenant, is therefore never reachable to delete
/// even when its surrogate id is known (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// CASCADE vs BLOCK (docs/adr/0012-resource-deletion-cascades-dependents.md): the deletion CASCADES its
/// dependent <see cref="EntityRelationship"/> edges (FK-backed), <c>visibility_rules</c> (polymorphic,
/// non-FK) and <c>asset_links</c> (polymorphic, non-FK) inside one transaction in
/// <see cref="EntityDeletionService"/>, rather than blocking the deletion while any remain — so a host
/// never has to hunt down dependents in its own workspace before deleting, and no dangling rule/link is
/// ever left behind (threats T2/T4/T5).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// load-then-authorize, fail-closed at every step and never leaking why — the same shape the
/// entity-relationship removal and scene/content-block write routes use, since an entity is host-prepared
/// workspace content:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed
///   mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id or entity id, and a
///   denied tenant resolution, are hidden as 404 (a caller who cannot see the tenant must not learn
///   whether the entity exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a non-member must not
///   learn the workspace's entities exist; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the delete role is 403. The delete role set is
///   <c>Owner</c>/<c>Admin</c>/<c>Host</c>/<c>CoHost</c> (csv/api_routes.csv;
///   docs/06_AUTHORIZATION_MATRIX.md — entities are host-prepared content, governed by the same
///   host-capable roles as scenes, content blocks and entity relationships). <see cref="MembershipRole"/>
///   is non-linear, so the check is EXACT set membership, never an ordering comparison.</item>
///   <item>Once authorized, an entity id that addresses no entity within the resolved workspace (an unknown
///   id, or one belonging to another workspace/tenant) is a SAFE 404 — deleting a non-existent entity
///   reveals nothing and changes nothing.</item>
/// </list>
///
/// On success the deletion is appended to the append-only audit log
/// (<see cref="LiveCore.Api.Audit.AuditAction.EntityDeleted"/>) by the service; it emits no realtime
/// session event (the event catalog defines no entity-deletion event and entity preparation is a
/// host-side workspace operation, faithful to the CORE-LIFE-001/002 precedent that audited but emitted no
/// event).
///
/// Persistence dependency: like the entity-relationship and scene endpoints, this uses the deletion
/// service, the tenant context resolver and the workspace member repository, which are registered only
/// when a database connection string is configured (see <c>Program.cs</c>). When persistence is off the
/// endpoint fails closed with 503 rather than crashing startup.
/// </summary>
internal static class EntityEndpoints
{
    /// <summary>Required value naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapDelete(
            "/{workspaceId}/entities/{entityId}",
            DeleteEntityAsync);

        return endpoints;
    }

    // DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={slug}
    private static async Task<IResult> DeleteEntityAsync(
        HttpContext httpContext,
        string workspaceId,
        string entityId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
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

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the entity-relationship removal and
        // by-session-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or entity id can never address a stored row; treat each as hidden (404),
        // never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntity();
        }

        if (!Guid.TryParse(entityId, out var entityGuid) || entityGuid == Guid.Empty)
        {
            return HiddenEntity();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing entity (threat T5).
            return HiddenEntity();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace (the parent workspace,
        // resolved first). A caller who is a member of the tenant but NOT of the workspace must not learn
        // the workspace's entities exist, so a missing membership is hidden as 404 (not 403) — the same
        // rule as the entity-relationship/scene write routes (threats T1/T5). The member's workspace role
        // then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenEntity();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to
        // know the workspace exists, but not to delete its entities). Entities are host-prepared content,
        // governed by the host-capable roles Owner/Admin/Host/CoHost (csv/api_routes.csv;
        // docs/06_AUTHORIZATION_MATRIX.md), the same set that creates scenes/content blocks and removes
        // entity relationships. MembershipRole is non-linear, so this is an EXACT set membership check,
        // never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized: delete the entity (and cascade its dependents + append the audit record) atomically.
        // The service loads the entity through the tenant- AND workspace-scoped FindByIdAsync, so an entity
        // in another workspace or tenant is never deleted even when its id is known; an unknown id is a
        // SAFE 404 that changes nothing (threats T1/T5).
        var now = timeProvider.GetUtcNow();
        var result = await deps.EntityDeletion
            .DeleteAsync(context.OrganizationId, workspaceGuid, entityGuid, context.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        return result == EntityDeletionResult.Deleted
            ? Results.NoContent()
            : HiddenEntity();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a
    /// database connection string is configured; when absent, the endpoint fails closed with 503 instead
    /// of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out EntityEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var entityDeletion = services.GetService<EntityDeletionService>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || entityDeletion is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new EntityEndpointDependencies(resolver, entityDeletion, workspaceMembers);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an <see cref="OidcPrincipal"/>,
    /// fail-closed: a failed mapping yields <see langword="false"/> (the caller returns 401). The mapping
    /// error reason is never echoed to the response (threat T7).
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
            detail: "Entity operations require persistence, which is not configured.");

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

    // Entity existence is hidden: a malformed workspace/entity id, an entity in a foreign or non-entitled
    // tenant, an entity in another workspace, an unknown entity, and an entity in a workspace the caller
    // does not belong to are ALL reported as 404, never distinguishable from each other and never echoing
    // the reason (docs/08; threats T1/T5).
    private static IResult HiddenEntity()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct EntityEndpointDependencies(
        TenantContextResolver Resolver,
        EntityDeletionService EntityDeletion,
        IWorkspaceMemberRepository WorkspaceMembers);
}
