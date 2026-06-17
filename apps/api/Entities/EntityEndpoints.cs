// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entities;

/// <summary>
/// HTTP endpoints of the Entities module's generic ENTITY routes. The entity CREATE, LIST and by-id READ
/// routes are CORE-ENT-006 ("Add the entity create list and read endpoints", the "Vertical Authoring and
/// Read API Completeness" epic); the entity DELETE route is CORE-LIFE-003 ("Implement entity deletion", the
/// "Resource Lifecycle and Deletion" epic). They make the platform usable for a vertical to author its world
/// through generic entities over the API, and they realize the documented request flow end-to-end:
/// "authentication middleware -> tenant/workspace context resolver -> endpoint -> authorization policy"
/// (docs/02_ARCHITECTURE.md), mirroring <see cref="Scenes.SceneEndpoints"/>'s workspace-scoped routes exactly.
///
/// Routes owned here, all under <c>/api/v1/workspaces/{workspaceId}/entities</c> (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET  /api/v1/workspaces/{workspaceId}/entities</c> — module Entities, roles
///   "workspace members". Lists the workspace's entities in the deterministic (UUIDv7) id order via
///   <see cref="IEntityRepository.ListByWorkspaceAsync"/>, PROJECTED BY the caller's workspace role
///   (<see cref="EntityProjection"/>). An entity IS content, so the host-content roles
///   (Owner/Admin/Host/CoHost) receive the full host shape (<see cref="EntityResponse"/>) and every other
///   role (the audience roles Participant/Observer, the audit role Auditor and any undefined value) receives
///   the host-only-field-stripped participant shape (<see cref="ParticipantEntityResponse"/>) — the
///   "View host-only content" row of docs/06, NOT the scene-style "View workspace metadata" split. Only the
///   per-entity SHAPE changes by role — every member still receives ALL of the workspace's entities; deciding
///   WHICH entities an audience may actually see (visibility filtering) is the entity-search read
///   (CORE-ENT-005) and the Visibility module's concern, not this projection.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/entities</c> — module Entities, roles
///   "Host,CoHost,Owner,Admin". Creates a generic entity via <see cref="EntityCreationService"/>: the
///   referenced entity type is resolved within the route's workspace (the same-workspace coupling the
///   database foreign key cannot enforce), the entity is created with a SERVER-MINTED surrogate id, and the
///   creation is appended to the append-only audit log (<see cref="Audit.AuditAction.EntityCreated"/>) — the
///   insert and the audit committing together in one transaction. Returns <c>201 Created</c> with the full
///   host <see cref="EntityResponse"/> (the create role is always a host-content role). A body-supplied type
///   id that does not resolve in the workspace is a <c>400</c>; an archived (read-only) workspace rejects the
///   create with a <c>409</c> (CORE-LIFE-009).</item>
///   <item><c>GET  /api/v1/workspaces/{workspaceId}/entities/{entityId}</c> — module Entities, roles
///   "workspace members". Reads ONE entity within the route's workspace via
///   <see cref="IEntityRepository.FindByIdAsync"/> (tenant- AND workspace-scoped), then PROJECTS it BY ROLE
///   through the SAME <see cref="EntityProjection"/> the list uses, so the by-id read can never diverge from
///   the list's host-vs-participant DTO split. A foreign/unknown entity is a hidden <c>404</c>.</item>
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}</c> — module Entities, roles
///   "Host,CoHost,Owner,Admin" (CORE-LIFE-003). Deletes ONE entity, addressed within its parent workspace,
///   CASCADING its dependent relationship edges, visibility rules and asset links via
///   <see cref="EntityDeletionService"/>, and appending an
///   <see cref="Audit.AuditAction.EntityDeleted"/> audit record. Returns <c>204 No Content</c>.</item>
/// </list>
///
/// Tenant resolution (mirrors the workspace by-id routes): the route path carries the <c>{workspaceId}</c>,
/// so the target organization is supplied by a required <c>organizationSlug</c> value (a query parameter for
/// the GET reads and the DELETE, a body field for the POST create — exactly like the scene routes) and turned
/// into a trusted <see cref="TenantContext"/> by <see cref="TenantContextResolver"/> (token organization
/// claim AND persisted organization membership — defence in depth, threat T5). The caller's WORKSPACE
/// membership in the route's workspace is then loaded and the operation is authorized by that workspace role.
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// load-then-authorize, fail-closed at every step and never leaking why — the same shape as the scene routes
/// (entities are host-prepared workspace content):
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed
///   mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id or entity id, and a
///   denied tenant resolution, are hidden as 404 (a caller who cannot see the tenant must not learn whether
///   the entity exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a non-member must not learn
///   the workspace's entities exist; threats T1/T5), never 403.</item>
///   <item>For the WRITE (create), a known workspace member who lacks the create role is 403. The create role
///   set is <c>Owner</c>/<c>Admin</c>/<c>Host</c>/<c>CoHost</c> (csv/api_routes.csv; entities are
///   host-prepared content, governed by the same host-capable roles as scenes and content blocks).
///   <see cref="MembershipRole"/> is non-linear, so the check is EXACT set membership, never an ordering
///   comparison. The LIST/READ are allowed to any workspace member; the member's role then drives the
///   host-vs-participant projection.</item>
/// </list>
///
/// Persistence dependency: like the scene endpoints, these use the repositories, the create/delete services
/// and the tenant context resolver, which are registered only when a database connection string is configured
/// (see <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than crashing
/// startup.
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

        workspaceScopedGroup.MapGet("/{workspaceId}/entities", ListEntitiesAsync);
        workspaceScopedGroup.MapPost("/{workspaceId}/entities", CreateEntityAsync);
        workspaceScopedGroup.MapGet("/{workspaceId}/entities/{entityId}", GetEntityByIdAsync);
        workspaceScopedGroup.MapDelete("/{workspaceId}/entities/{entityId}", DeleteEntityAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces/{workspaceId}/entities?organizationSlug={slug}&limit={n}&offset={n}
    private static async Task<IResult> ListEntitiesAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = Page.LimitQuery)] string? limit,
        [FromQuery(Name = Page.OffsetQuery)] string? offset,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntity();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (threat T5).
            return HiddenEntity();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. The list is allowed to
        // ANY membership role ("workspace members"), so any membership suffices; a non-member is hidden as 404
        // (not 403) so resource existence is not leaked (threats T1/T5). The member's actual workspace ROLE
        // then drives the host-vs-participant projection below, so this loads the membership row (FindAsync).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenEntity();
        }

        // Authorized. Only now validate the optional paging parameters, so a non-member never receives
        // request-shape feedback (mirrors the audit read): a present-but-malformed limit/offset is a 400, an
        // absent value uses the default page.
        if (!Page.TryResolveLimit(limit, out var pageLimit, out var limitError))
        {
            return ValidationError(limitError);
        }

        if (!Page.TryResolveOffset(offset, out var pageOffset, out var offsetError))
        {
            return ValidationError(offsetError);
        }

        // The entities are returned in the deterministic (UUIDv7) id order the repository enforces, PROJECTED
        // BY ROLE: an entity IS content, so the host-content roles (Owner/Admin/Host/CoHost) receive the full
        // host shape and every other role (audience, audit, undefined) the stripped participant shape
        // (EntityProjection). The per-entity SHAPE differs by role (deciding WHICH entities an audience may see
        // is the entity-search/Visibility concern, not this).
        //
        // BOUNDED (CORE-DX-003): the page is read through the tenant- and workspace-scoped paged repository,
        // fetching ONE extra row so HasMore is set without a second COUNT; the trimmed page is then
        // role-projected into the PageResponse envelope. A list can never return the whole table (threat T9).
        var rows = await deps.Entities
            .ListPageByWorkspaceAsync(context.OrganizationId, workspaceGuid, pageOffset, pageLimit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageLimit;
        var pageRows = hasMore ? rows.Take(pageLimit).ToArray() : rows;

        return Results.Ok(EntityProjection.ProjectPage(pageRows, member.Role, pageOffset, pageLimit, hasMore));
    }

    // GET /api/v1/workspaces/{workspaceId}/entities/{entityId}?organizationSlug={slug}
    private static async Task<IResult> GetEntityByIdAsync(
        HttpContext httpContext,
        string workspaceId,
        string entityId,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or entity id can never address a stored row; treat each as hidden (404).
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
            // Not entitled to the tenant: hide as 404 (threat T5).
            return HiddenEntity();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. The read is allowed to
        // ANY membership role ("workspace members"); a non-member is hidden as 404 (not 403) so resource
        // existence is not leaked (threats T1/T5). The member's role drives the projection below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenEntity();
        }

        // Load the entity through the tenant- AND workspace-scoped FindByIdAsync (it leads with
        // organization_id then workspace_id then the entity id), so an entity in another workspace or tenant
        // is never returned even when the surrogate id matches; an unknown id is a hidden 404 (threats T1/T5).
        var entity = await deps.Entities
            .FindByIdAsync(context.OrganizationId, workspaceGuid, entityGuid, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return HiddenEntity();
        }

        // PROJECT BY ROLE through the SAME projector the list uses, so the by-id read can never diverge from
        // the list's host-vs-participant DTO split (an undefined role falls closed to the stripped shape).
        return Results.Ok(EntityProjection.ProjectOne(entity, member.Role));
    }

    // POST /api/v1/workspaces/{workspaceId}/entities
    private static async Task<IResult> CreateEntityAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateEntityRequest? request,
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

        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // Validate the entity inputs before touching the tenant, so a broken body is a 400 regardless of
        // authorization. The entity type id is a required reference; a missing/malformed/empty id can never
        // address a stored type, so it is rejected as a 400 (the existence of the type within the workspace is
        // checked later, by the create service, AFTER authorization — see below). The name and attribute
        // values are template-/host-supplied DATA validated for SHAPE only (the template boundary, docs/04);
        // the rejected content is never echoed back (threat T7). The attribute values default to an empty JSON
        // object when omitted, so authoring an entity with no attributes yet needs no body field.
        if (!Guid.TryParse(request.EntityTypeId, out var entityTypeGuid) || entityTypeGuid == Guid.Empty)
        {
            return ValidationError("A valid entity type is required.");
        }

        var name = request.Name?.Trim();
        if (!Entity.IsValidName(name))
        {
            return ValidationError("A valid entity name is required.");
        }

        var attributeValues = string.IsNullOrWhiteSpace(request.AttributeValues)
            ? "{}"
            : request.AttributeValues.Trim();
        if (!Entity.IsValidAttributeValues(attributeValues))
        {
            return ValidationError("Valid entity attribute values are required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntity();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenEntity();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A caller who is a member
        // of the tenant but NOT of the workspace must not learn the workspace exists, so a missing membership
        // is hidden as 404 (not 403) — the same rule as the scene create route (threats T1/T5). The member's
        // workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenEntity();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to know
        // the workspace exists, but not to create an entity in it). "Create entity" is "Host,CoHost,Owner,Admin"
        // (csv/api_routes.csv; entities are host-prepared content, the same host-capable set that creates
        // scenes/content blocks). MembershipRole is non-linear, so this is an EXACT set membership check.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Read-only when the parent workspace is archived (CORE-LIFE-009): creating an entity is an authoring
        // mutation, so an archived workspace rejects it with a 409 Conflict that creates nothing. The workspace
        // is loaded within the resolved tenant (the membership above already proves it exists, so a null here
        // is defensive and hidden as 404); the check is placed AFTER role authorization so a member who lacks
        // the create role still gets a 403 and never learns the archived state (threat T7), mirroring the scene
        // create handler.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenEntity();
        }

        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Authorized: create the entity (resolving its type within the workspace, persisting it and appending
        // the EntityCreated audit record) atomically. The service resolves the type through the tenant- AND
        // workspace-scoped FindByIdAsync, so a type in another workspace/tenant — or an unknown type — is never
        // borrowed; that unresolved-type outcome is a 400 (the body-supplied reference does not resolve in the
        // caller's authorized workspace; the same response for unknown and foreign types leaks nothing).
        var now = timeProvider.GetUtcNow();
        var result = await deps.EntityCreation
            .CreateAsync(
                context.OrganizationId,
                workspaceGuid,
                entityTypeGuid,
                name!,
                attributeValues,
                context.UserProfileId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == EntityCreationStatus.UnknownEntityType)
        {
            return ValidationError("A valid entity type is required.");
        }

        var entity = result.Entity!;
        var response = EntityResponse.From(entity);
        return Results.Created($"/api/v1/workspaces/{workspaceGuid}/entities/{entity.Id}", response);
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
        // resolved first). A caller who is a member of the tenant but NOT of the workspace must not learn the
        // workspace's entities exist, so a missing membership is hidden as 404 (not 403) — the same rule as the
        // scene/content-block write routes (threats T1/T5). The member's workspace role then drives the role
        // check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenEntity();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to know
        // the workspace exists, but not to delete its entities). Entities are host-prepared content, governed
        // by the host-capable roles Owner/Admin/Host/CoHost (csv/api_routes.csv; docs/06_AUTHORIZATION_MATRIX.md),
        // the same set that creates scenes/content blocks and removes entity relationships. MembershipRole
        // is non-linear, so the check is EXACT set membership, never an ordering comparison.
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
        var entities = services.GetService<IEntityRepository>();
        var entityCreation = services.GetService<EntityCreationService>();
        var entityDeletion = services.GetService<EntityDeletionService>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || entities is null
            || entityCreation is null
            || entityDeletion is null
            || workspaces is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new EntityEndpointDependencies(
            resolver, entities, entityCreation, entityDeletion, workspaces, workspaceMembers);
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Entity operations require persistence, which is not configured.");

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

    // The parent workspace is archived and therefore read-only (CORE-LIFE-009), so creating an entity in it is
    // refused. The caller is authorized; the workspace's lifecycle state, not the caller, is the reason, so
    // this is a 409 Conflict. The detail names only the generic state and leaks no tenant data (threat T7).
    private static IResult ArchivedReadOnly()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.WorkspaceArchived,
            title: "Conflict",
            detail: "The workspace is archived and is read-only.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // Entity existence is hidden: a malformed workspace/entity id, an entity in a foreign or non-entitled
    // tenant, an entity in another workspace, an unknown entity, and an entity in a workspace the caller
    // does not belong to are ALL reported as 404, never distinguishable from each other and never echoing
    // the reason (docs/08; threats T1/T5).
    private static IResult HiddenEntity()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct EntityEndpointDependencies(
        TenantContextResolver Resolver,
        IEntityRepository Entities,
        EntityCreationService EntityCreation,
        EntityDeletionService EntityDeletion,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers);
}
