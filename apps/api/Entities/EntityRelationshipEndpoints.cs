// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entities;

/// <summary>
/// HTTP endpoints of the Entities module's relationship (graph edge) routes. The edge REMOVAL is CORE-LIFE-002
/// (the "Resource Lifecycle and Deletion" epic); the edge CREATE and the read LIST are CORE-ENT-008 (the
/// "Entity Graph and Search Completeness" epic), which makes the entity-relationship graph AUTHORABLE and
/// READABLE, not only deletable. Until CORE-ENT-008 an <see cref="EntityRelationship"/> edge could only be
/// removed — a relationship could never be created or read (ARC-GAP-118) — so this adds the missing create and
/// read halves of the directed graph edge.
///
/// Routes owned here, all under <c>/api/v1/workspaces/{workspaceId}/entity-relationships</c>
/// (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST   /api/v1/workspaces/{workspaceId}/entity-relationships</c> — module Entities, roles
///   "Host,CoHost,Owner,Admin" (CORE-ENT-008). Creates a directed edge via
///   <see cref="EntityRelationshipCreationService"/>: BOTH endpoint entities are resolved within the route's
///   workspace (the same-workspace coupling the database foreign keys cannot enforce), then
///   <see cref="EntityRelationship.Create"/> + <see cref="IEntityRelationshipRepository.AddAsync"/> with a
///   SERVER-MINTED surrogate id. Returns <c>201 Created</c> with the <see cref="EntityRelationshipResponse"/>.
///   A duplicate of the same directed edge of the same kind is a <c>409</c>; an endpoint entity that does not
///   resolve in the workspace (unknown or foreign) is a <c>400</c>; a self-loop or a malformed/missing id or
///   kind is a <c>400</c>; an archived (read-only) workspace rejects the create with a <c>409</c>
///   (CORE-LIFE-009).</item>
///   <item><c>GET    /api/v1/workspaces/{workspaceId}/entity-relationships</c> — module Entities, roles
///   "Host,CoHost,Owner,Admin" (CORE-ENT-008). Lists the workspace's edges in the deterministic (UUIDv7) id
///   order via <see cref="IEntityRelationshipRepository.ListByWorkspaceAsync"/>, OR — when an optional
///   <c>?entityId=</c> is supplied — only the edges TOUCHING that entity (source OR target) via
///   <see cref="IEntityRelationshipRepository.ListByEntityAsync"/>, each as an
///   <see cref="EntityRelationshipResponse"/>. A relationship edge is a structural/authoring graph artifact
///   (no free-form content), so — like the entity-type reads — the read is restricted to the authoring roles
///   and there is a SINGLE projection (no host-vs-participant split).</item>
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}</c> — module
///   Entities, roles "Host,CoHost,Owner,Admin" (CORE-LIFE-002). Removes ONE directed edge, addressed within
///   its parent workspace. Returns <c>204 No Content</c> on success.</item>
/// </list>
///
/// WORKSPACE-SCOPED LOOKUP (the headline requirement of both stories): the relationship's endpoint foreign
/// keys do NOT DB-enforce that the edge, its source and its target all live in the same workspace (mirrors
/// <see cref="Entity.EntityTypeId"/> / <c>ContentBlock.SceneId</c>), so the parent workspace is resolved FIRST
/// and everything is then scoped THROUGH it: the route pins the <c>{workspaceId}</c> in the path (exactly like
/// the member-removal route), the target organization is supplied by a required <c>organizationSlug</c> value
/// (a query parameter for the GET reads and the DELETE, a body field for the POST create), and the edge — on
/// create, its two endpoints; on read, the listing; on delete, the edge — is resolved with a lookup that
/// scopes by <c>organization_id</c> THEN <c>workspace_id</c>. An edge or endpoint that lives in another
/// workspace, or in a workspace owned by another tenant, is therefore never reachable even when its surrogate
/// id is known (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// load-then-authorize, fail-closed at every step and never leaking why — the same shape the scene /
/// content-block write routes use, since an entity relationship is host-prepared workspace content:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed
///   mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id or relationship id, and a
///   denied tenant resolution, are hidden as 404 (a caller who cannot see the tenant must not learn whether
///   the edge exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a non-member must not learn
///   the workspace's edges exist; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the authoring role is 403. The role set is
///   <c>Owner</c>/<c>Admin</c>/<c>Host</c>/<c>CoHost</c> for EVERY route here (csv/api_routes.csv;
///   docs/06_AUTHORIZATION_MATRIX.md — entity relationships are host-prepared content/structure, governed by
///   the same host-capable roles as scenes and content blocks). <see cref="MembershipRole"/> is non-linear, so
///   the check is EXACT set membership, never an ordering comparison.</item>
///   <item>Once authorized, a relationship id that addresses no edge within the resolved workspace (DELETE),
///   or an endpoint id that resolves to no entity within it (POST), is a SAFE 404 / 400 — it reveals nothing
///   and changes nothing.</item>
/// </list>
///
/// The create and remove of an edge record a recorded fact only; they add no event and no audit record (the
/// edge model carries no visibility surface and the threat model lists no audit control for relationship
/// authoring), staying faithful to the CORE-ENT-003 precedent where adding an edge emitted neither.
///
/// Persistence dependency: like the scene endpoints, these use the repositories, the create service and the
/// tenant context resolver, which are registered only when a database connection string is configured (see
/// <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than crashing
/// startup.
/// </summary>
internal static class EntityRelationshipEndpoints
{
    /// <summary>Required value naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    /// <summary>Optional value restricting the list to the edges touching one entity.</summary>
    private const string _entityIdQuery = "entityId";

    public static IEndpointRouteBuilder MapEntityRelationshipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapGet(
            "/{workspaceId}/entity-relationships",
            ListEntityRelationshipsAsync);

        workspaceScopedGroup.MapPost(
            "/{workspaceId}/entity-relationships",
            CreateEntityRelationshipAsync);

        workspaceScopedGroup.MapDelete(
            "/{workspaceId}/entity-relationships/{relationshipId}",
            RemoveEntityRelationshipAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces/{workspaceId}/entity-relationships?organizationSlug={slug}&entityId={guid}
    private static async Task<IResult> ListEntityRelationshipsAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromQuery(Name = _entityIdQuery)] string? entityId,
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
            return HiddenRelationship();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (threat T5).
            return HiddenRelationship();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A non-member is hidden as
        // 404 (not 403) so the workspace's edges are not leaked (threats T1/T5). The member's workspace role
        // then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenRelationship();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403. An entity
        // relationship is an authoring/graph artifact (not audience content), so the read is restricted to the
        // host-capable roles Owner/Admin/Host/CoHost — the SAME set as the create/delete and as the entity-type
        // reads. MembershipRole is non-linear, so this is an EXACT set membership check.
        if (!HasAuthoringRole(member))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the optional entityId filter, so a non-member never receives
        // request-shape feedback (mirrors the entity list's paging-parameter validation): a present-but-malformed
        // entityId is a 400; an absent value lists the whole workspace.
        IReadOnlyList<EntityRelationship> relationships;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            // List EVERY edge of the workspace in the deterministic (UUIDv7) id order, tenant- AND
            // workspace-scoped (the predicate leads with organization_id then workspace_id), so a foreign
            // tenant's or workspace's edges are never returned (threat T5/T1).
            relationships = await deps.EntityRelationships
                .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (Guid.TryParse(entityId, out var entityGuid) && entityGuid != Guid.Empty)
        {
            // List only the edges TOUCHING the given entity as source OR target, still tenant- AND
            // workspace-scoped. An entityId that addresses no edge in the workspace simply yields an empty list.
            relationships = await deps.EntityRelationships
                .ListByEntityAsync(context.OrganizationId, workspaceGuid, entityGuid, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return ValidationError($"The '{_entityIdQuery}' value must be a valid entity id.");
        }

        var response = new List<EntityRelationshipResponse>(relationships.Count);
        foreach (var relationship in relationships)
        {
            response.Add(EntityRelationshipResponse.From(relationship));
        }

        return Results.Ok(response);
    }

    // POST /api/v1/workspaces/{workspaceId}/entity-relationships
    private static async Task<IResult> CreateEntityRelationshipAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateEntityRelationshipRequest? request,
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

        // Validate the edge inputs before touching the tenant, so a broken body is a 400 regardless of
        // authorization (mirrors the entity create). The endpoint ids are required references; a
        // missing/malformed/empty id can never address a stored entity, so it is rejected as a 400 (the
        // existence of each endpoint WITHIN the workspace is checked later, by the create service, AFTER
        // authorization). The kind is template-/host-supplied DATA validated for SHAPE only (the template
        // boundary, docs/04); the rejected value is never echoed back (threat T7).
        if (!Guid.TryParse(request.SourceEntityId, out var sourceGuid) || sourceGuid == Guid.Empty)
        {
            return ValidationError("A valid source entity is required.");
        }

        if (!Guid.TryParse(request.TargetEntityId, out var targetGuid) || targetGuid == Guid.Empty)
        {
            return ValidationError("A valid target entity is required.");
        }

        // Self-loop invariant (mirrors the aggregate): an edge connects two DISTINCT entities, so a source that
        // equals the target is rejected as a 400 before the aggregate would throw.
        if (sourceGuid == targetGuid)
        {
            return ValidationError("A relationship must connect two distinct entities.");
        }

        // Canonicalize the kind (trim + lower-case) and validate its generic slug shape, so an invalid kind is a
        // clean 400 rather than a throw inside the aggregate factory.
        var relationshipKind = request.RelationshipKind?.Trim().ToLowerInvariant();
        if (!EntityRelationship.IsValidRelationshipKind(relationshipKind))
        {
            return ValidationError("A valid relationship kind is required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenRelationship();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenRelationship();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A caller who is a member of
        // the tenant but NOT of the workspace must not learn the workspace exists, so a missing membership is
        // hidden as 404 (not 403) — the same rule as the scene create route (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenRelationship();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403. "Create entity
        // relationship" is "Host,CoHost,Owner,Admin" (csv/api_routes.csv; entities are host-prepared content,
        // the same host-capable set that creates scenes/content blocks/entities). MembershipRole is non-linear,
        // so this is an EXACT set membership check.
        if (!HasAuthoringRole(member))
        {
            return Forbidden();
        }

        // Read-only when the parent workspace is archived (CORE-LIFE-009): creating an edge is an authoring
        // mutation, so an archived workspace rejects it with a 409 Conflict that creates nothing. The workspace
        // is loaded within the resolved tenant (the membership above already proves it exists, so a null here is
        // defensive and hidden as 404); the check is placed AFTER role authorization so a member who lacks the
        // create role still gets a 403 and never learns the archived state (threat T7), mirroring the entity
        // create handler.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenRelationship();
        }

        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Authorized: create the edge. The service resolves BOTH endpoints through the tenant- AND
        // workspace-scoped FindByIdAsync (the same-workspace coupling the database foreign keys cannot enforce),
        // so an endpoint in another workspace/tenant — or an unknown endpoint — is never borrowed (mapped to a
        // 400 below; the same response for unknown and foreign leaks nothing, threats T1/T5). A duplicate of the
        // same directed edge of the same kind is a 409.
        var now = timeProvider.GetUtcNow();
        var creation = await deps.Creation
            .CreateAsync(context.OrganizationId, workspaceGuid, sourceGuid, targetGuid, relationshipKind!, now, cancellationToken)
            .ConfigureAwait(false);

        switch (creation.Status)
        {
            case EntityRelationshipCreationStatus.UnknownSourceEntity:
            case EntityRelationshipCreationStatus.UnknownTargetEntity:
                return ValidationError("A valid source and target entity in this workspace are required.");
            case EntityRelationshipCreationStatus.Duplicate:
                return Duplicate();
            default:
                var relationship = creation.Relationship!;
                var response = EntityRelationshipResponse.From(relationship);
                return Results.Created(
                    $"/api/v1/workspaces/{workspaceGuid}/entity-relationships/{relationship.Id}", response);
        }
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

        // Object-level authorization: the caller must be a member of THIS workspace (the parent workspace,
        // resolved first). A caller who is a member of the tenant but NOT of the workspace must not learn the
        // workspace's edges exist, so a missing membership is hidden as 404 (not 403) — the same rule as the
        // scene/content write routes (threats T1/T5). The member's workspace role then drives the role check
        // below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenRelationship();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to know
        // the workspace exists, but not to remove its relationships). Entity relationships are host-prepared
        // content, governed by the host-capable roles Owner/Admin/Host/CoHost (csv/api_routes.csv;
        // docs/06_AUTHORIZATION_MATRIX.md), the same set that creates scenes and content blocks. MembershipRole
        // is non-linear, so this is an EXACT set membership check, never a >/< ordering comparison.
        if (!HasAuthoringRole(member))
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
    /// Whether the workspace member holds an authoring role for entity relationships — the host-capable set
    /// Owner/Admin/Host/CoHost (csv/api_routes.csv; docs/06_AUTHORIZATION_MATRIX.md). <see cref="MembershipRole"/>
    /// is non-linear, so this is an EXACT set membership check, never a &gt;/&lt; ordering comparison; any other
    /// role falls closed (denied).
    /// </summary>
    private static bool HasAuthoringRole(WorkspaceMember member)
        => member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost);

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
        var entities = services.GetService<IEntityRepository>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var creation = services.GetService<EntityRelationshipCreationService>();

        if (resolver is null
            || entityRelationships is null
            || entities is null
            || workspaces is null
            || workspaceMembers is null
            || creation is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new EntityRelationshipEndpointDependencies(
            resolver, entityRelationships, entities, workspaces, workspaceMembers, creation);
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Entity relationship operations require persistence, which is not configured.");

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

    // The parent workspace is archived and therefore read-only (CORE-LIFE-009), so creating an edge in it is
    // refused. The caller is authorized; the workspace's lifecycle state, not the caller, is the reason, so
    // this is a 409 Conflict. The detail names only the generic state and leaks no tenant data (threat T7).
    private static IResult ArchivedReadOnly()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.WorkspaceArchived,
            title: "Conflict",
            detail: "The workspace is archived and is read-only.");

    // The workspace already holds the same directed edge of the same kind. The caller is authorized; the
    // existing edge, not the caller, is the reason, so this is a 409 Conflict. The detail names only the
    // generic conflict and leaks no tenant data (threat T7).
    private static IResult Duplicate()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.DuplicateResource,
            title: "Conflict",
            detail: "A relationship of this kind between these entities already exists.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // Relationship existence is hidden: a malformed workspace/relationship id, an edge in a foreign or
    // non-entitled tenant, an edge in another workspace, an unknown edge, and an edge in a workspace
    // the caller does not belong to are ALL reported as 404, never distinguishable from each other and
    // never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenRelationship()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct EntityRelationshipEndpointDependencies(
        TenantContextResolver Resolver,
        IEntityRelationshipRepository EntityRelationships,
        IEntityRepository Entities,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers,
        EntityRelationshipCreationService Creation);
}
