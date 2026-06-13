using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Scenes;

/// <summary>
/// HTTP endpoints of the Scenes module. The scene content read/create routes are CORE-SCENE-003
/// ("Implement scene content APIs") and the by-scene-id read is CORE-API-007; the scene DELETE route is
/// CORE-LIFE-005 ("Implement scene deletion", the "Resource Lifecycle and Deletion" epic). They realize the
/// documented request flow end-to-end for the workspace-scoped and by-scene-id scene routes:
/// "authentication middleware -> tenant/workspace context resolver -> endpoint -> authorization policy"
/// (docs/02_ARCHITECTURE.md), mirroring <see cref="Workspaces.WorkspaceEndpoints"/>'s
/// <c>{workspaceId}</c>-in-path routes exactly.
///
/// The DELETE route (CORE-LIFE-005) — <c>DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId}</c>,
/// roles "Host,CoHost,Owner,Admin" — deletes ONE scene, addressed within its parent workspace, cascading
/// its child content blocks and the dependent visibility rules / asset links, RE-PACKING the remaining
/// scenes' ordering so there is no gap (the SCENE-001 ordering logic, reused) and appending an append-only
/// <see cref="LiveCore.Api.Audit.AuditAction.SceneDeleted"/> audit record, all atomically through
/// <see cref="SceneDeletionService"/> (cascade, not block;
/// docs/adr/0012-resource-deletion-cascades-dependents.md). Returns <c>204 No Content</c> on success;
/// deleting a non-existent scene is a safe hidden-404.
///
/// Routes owned by this story (csv/api_routes.csv lines 15-16):
/// <list type="bullet">
///   <item><c>GET  /api/v1/workspaces/{workspaceId}/scenes</c> — module Scenes, roles
///   "workspace members". Lists the workspace's scenes in the deterministic
///   (scene_order, id) order via <see cref="ISceneRepository.ListByWorkspaceAsync"/>,
///   PROJECTED BY the caller's workspace role (CORE-SCENE-004, honoring the "Projection
///   by role" note in csv/api_routes.csv line 15). <see cref="SceneProjection"/>
///   returns the full host shape (<see cref="SceneResponse"/>) to the host-capable and
///   metadata-entitled roles (Owner/Admin/Host/CoHost/Auditor) and the
///   host-only-field-stripped participant shape (<see cref="ParticipantSceneResponse"/>)
///   to the audience roles (Participant/Observer). Only the per-scene SHAPE changes by
///   role — every member still receives ALL of the workspace's scenes; deciding WHICH
///   scenes an audience may see (audience calculation, visibility filtering) is the
///   Visibility module's later CORE-VIS-* concern, not this projection.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/scenes</c> — module Scenes, roles
///   "Host,CoHost,Owner,Admin". Creates a scene via <see cref="Scene.Create"/> +
///   <see cref="ISceneRepository.AddAsync"/>. The order is assigned SERVER-SIDE as
///   append-to-end (the next order after the current maximum in the workspace; an empty
///   workspace gets order 0), so a client can never choose, skip or collide a position.
///   No reorder route exists in csv/api_routes.csv, so reorder is out of scope.</item>
///   <item><c>GET  /api/v1/scenes/{sceneId}</c> — module Scenes, roles "workspace members"
///   (CORE-API-007, the documented-not-built by-scene-id read of docs/08_API_CONTRACTS.md).
///   Reads ONE scene by id within the query-supplied organization via
///   <see cref="ISceneRepository.FindByIdInOrganizationAsync"/> (the same org-scoped lookup
///   the content-block route uses), discovers the scene's own workspace from the loaded row
///   AFTER the tenant boundary has been enforced, authorizes the caller's membership in that
///   workspace, then PROJECTS the scene BY ROLE through the SAME
///   <see cref="SceneProjection"/> the list uses — the host shape
///   (<see cref="SceneResponse"/>) to the host-capable/metadata roles and the stripped
///   participant shape (<see cref="ParticipantSceneResponse"/>) to the audience roles — so
///   the by-id read can never diverge from the list's host-vs-participant DTO split.</item>
/// </list>
///
/// Tenant resolution (mirrors the workspace by-id routes): the route path carries the
/// <c>{workspaceId}</c>, so the target organization is supplied by a required
/// <c>organizationSlug</c> value (a query parameter for the GET read, a body field for
/// the POST write — exactly like the workspace by-id read and the workspace create) and
/// turned into a trusted <see cref="TenantContext"/> by
/// <see cref="TenantContextResolver"/> (token organization claim AND persisted
/// organization membership — defence in depth, threat T5). The caller's WORKSPACE
/// membership in the route's workspace is then loaded and the operation is authorized by
/// that workspace role.
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5), load-then-authorize, fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's
///   <see cref="ClaimsPrincipal"/>; a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id and a
///   denied tenant resolution are hidden as 404 (a caller who cannot see the tenant must
///   not learn whether the workspace exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a
///   non-member must not learn the workspace's scenes exist — the same object-level rule
///   as the workspace read-one route; threats T1/T5), never 403.</item>
///   <item>For the write, a known workspace member who lacks the scene-create role is 403.
///   <see cref="MembershipRole"/> is non-linear, so the role check is EXACT (Host, CoHost,
///   Owner or Admin), never an ordering comparison.</item>
/// </list>
///
/// Persistence dependency: like the workspace endpoints, these use the repositories and
/// the tenant context resolver, which are registered only when a database connection
/// string is configured (see <c>Program.cs</c>). When persistence is off the endpoints
/// fail closed with 503 rather than crashing startup.
/// </summary>
internal static class SceneEndpoints
{
    /// <summary>Required value naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapSceneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a
        // missing/invalid token is challenged as 401 before any handler runs.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapGet("/{workspaceId}/scenes", ListScenesAsync);
        workspaceScopedGroup.MapPost("/{workspaceId}/scenes", CreateSceneAsync);
        workspaceScopedGroup.MapDelete("/{workspaceId}/scenes/{sceneId}", DeleteSceneAsync);

        // The by-scene-id read group (CORE-API-007): the route path carries only the
        // {sceneId}, so the target organization is a required ?organizationSlug= query
        // parameter exactly like the by-session-id and content-block routes.
        var sceneScopedGroup = endpoints
            .MapGroup("/api/v1/scenes")
            .RequireAuthorization();

        sceneScopedGroup.MapGet("/{sceneId}", GetSceneByIdAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces/{workspaceId}/scenes?organizationSlug={slug}
    private static async Task<IResult> ListScenesAsync(
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

        // The target organization is required and supplied by the request; the route
        // path carries no organization, so it is a query parameter exactly like the
        // workspace by-id read.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace id can never address a stored workspace; treat it as
        // hidden (404), never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent
            // tenant is indistinguishable from a missing workspace (docs/08; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace.
        // The list is allowed to ANY membership role ("workspace members"), so any
        // membership suffices; a non-member is hidden as 404 (not 403) so resource
        // existence is not leaked — the same rule as the workspace read-one route
        // (threats T1/T5). The member's actual workspace ROLE then drives the
        // host-vs-participant projection below, so this loads the membership row
        // (FindAsync) rather than the boolean IsMemberAsync — the same membership
        // lookup the create handler uses.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The scenes are returned in the deterministic (scene_order, id) order the
        // repository enforces. The list is then PROJECTED BY ROLE (CORE-SCENE-004,
        // csv/api_routes.csv line 15 "Projection by role"): host-capable and
        // metadata-entitled roles (Owner/Admin/Host/CoHost/Auditor) receive the full
        // host shape (SceneResponse); the audience roles (Participant/Observer, whose
        // "View workspace metadata" is "limited" in docs/06_AUTHORIZATION_MATRIX.md)
        // receive the host-only-field-stripped participant shape
        // (ParticipantSceneResponse). Only the per-scene SHAPE changes by role — every
        // member still receives ALL of the workspace's scenes (the SET is unchanged;
        // deciding WHICH scenes an audience may see is the Visibility module's later
        // concern, not this projection). MembershipRole is non-linear, so the
        // classification is EXACT set membership, never a >/< comparison
        // (SceneProjection.ReceivesHostShape).
        var scenes = await deps.Scenes
            .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);

        var response = SceneProjection.Project(scenes, member.Role);
        return Results.Ok(response);
    }

    // GET /api/v1/scenes/{sceneId}?organizationSlug={slug}
    private static async Task<IResult> GetSceneByIdAsync(
        HttpContext httpContext,
        string sceneId,
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

        // The target organization is required and supplied by the request; the route
        // path carries no organization, so it is a query parameter exactly like the
        // by-session-id and content-block routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed scene id can never address a stored scene; treat it as hidden
        // (404), never echoing back why.
        if (!Guid.TryParse(sceneId, out var sceneGuid) || sceneGuid == Guid.Empty)
        {
            return HiddenScene();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the scene as 404 so a foreign or
            // non-existent tenant is indistinguishable from a missing scene (docs/08;
            // threat T5).
            return HiddenScene();
        }

        var context = resolution.Context;

        // Load the scene WITHIN the resolved tenant. The lookup leads with the
        // organization id, so a scene in another tenant is never returned even when the
        // surrogate id matches; a cross-tenant or unknown scene is hidden as 404 (threats
        // T1/T5). The scene's own workspace id is then discovered from the loaded row,
        // AFTER the tenant boundary has been enforced — the same shape as the
        // content-block route (ISceneRepository.FindByIdInOrganizationAsync).
        var scene = await deps.Scenes
            .FindByIdInOrganizationAsync(context.OrganizationId, sceneGuid, cancellationToken)
            .ConfigureAwait(false);
        if (scene is null)
        {
            return HiddenScene();
        }

        // Object-level authorization: the caller must be a member of the SCENE'S own
        // workspace. The read is allowed to ANY membership role (the same "workspace
        // members" rule as the scene list), so any membership suffices; a non-member is
        // hidden as 404 (not 403) so resource existence is not leaked (threats T1/T5). The
        // member's actual workspace ROLE then drives the host-vs-participant projection
        // below, so this loads the membership row (FindAsync) scoped by organization id
        // then the scene's own workspace id — a control role held in a DIFFERENT workspace
        // never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, scene.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenScene();
        }

        // The scene is PROJECTED BY ROLE through the SAME projector the list uses
        // (CORE-SCENE-004 / CORE-API-007): host-capable and metadata-entitled roles
        // (Owner/Admin/Host/CoHost/Auditor) receive the full host shape (SceneResponse);
        // the audience roles (Participant/Observer) receive the host-only-field-stripped
        // participant shape (ParticipantSceneResponse). MembershipRole is non-linear, so
        // the classification is EXACT set membership, never a >/< comparison, and an
        // undefined role falls closed to the stripped shape (SceneProjection.ProjectOne).
        var response = SceneProjection.ProjectOne(scene, member.Role);
        return Results.Ok(response);
    }

    // POST /api/v1/workspaces/{workspaceId}/scenes
    private static async Task<IResult> CreateSceneAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateSceneRequest? request,
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

        // Validate the scene inputs before touching the tenant, so a broken body is a
        // 400 regardless of authorization.
        if (!Scene.IsValidTitle(request.Title?.Trim()))
        {
            return ValidationError("A valid scene title is required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it as
        // hidden (404), never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A
        // caller who is a member of the tenant but NOT of the workspace must not learn
        // the workspace exists, so a missing membership is hidden as 404 (not 403) — the
        // same rule as the workspace read-one route (threats T1/T5). The member's
        // workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The caller is a known member of the workspace, so an insufficient role is a
        // 403 (authorized to know the workspace exists, but not to create a scene in it).
        // "Create scene" is "Host,CoHost,Owner,Admin" (csv/api_routes.csv;
        // docs/06_AUTHORIZATION_MATRIX.md). MembershipRole is non-linear, so this is an
        // EXACT set membership check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Append-to-end ordering (CORE-SCENE-001 deferred this to the endpoint story):
        // the new scene's position is the next order after the current maximum in the
        // workspace. An empty workspace gets the first order (0). The order is assigned
        // server-side; the client never supplies one. The scenes list is the tenant-
        // and workspace-scoped, deterministic (scene_order, id) order, so the maximum is
        // the order of the last element.
        var existingScenes = await deps.Scenes
            .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        var nextOrder = existingScenes.Count == 0
            ? 0
            : existingScenes.Max(scene => scene.Order) + 1;

        var now = timeProvider.GetUtcNow();
        var scene = Scene.Create(context.OrganizationId, workspaceGuid, request.Title!.Trim(), nextOrder, now);

        // A scene has no uniqueness constraint, so there is no 409 outcome to translate;
        // AddAsync always returns Added on success (a foreign-key violation would surface
        // as a DbUpdateException, which the membership check above already precludes for a
        // resolved, existing workspace).
        await deps.Scenes.AddAsync(scene, cancellationToken).ConfigureAwait(false);

        var response = SceneResponse.From(scene);
        return Results.Created($"/api/v1/scenes/{scene.Id}", response);
    }

    // DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId}?organizationSlug={slug}
    //
    // Deletes ONE scene from its parent workspace (CORE-LIFE-005, the "Resource Lifecycle and Deletion"
    // epic). The flow mirrors the create handler above (the same fail-closed, load-then-authorize,
    // hidden-404 shape) and the entity deletion route: the parent workspace is resolved FIRST (the route
    // pins {workspaceId}, the tenant comes from the required ?organizationSlug=), the caller is authorized by
    // their role in that workspace, and only then is the scene deleted (its child content blocks and the
    // dependent visibility rules / asset links cascaded, the remaining scenes' ordering re-packed and the
    // deletion audited) atomically through the tenant- and workspace-scoped deletion service.
    private static async Task<IResult> DeleteSceneAsync(
        HttpContext httpContext,
        string workspaceId,
        string sceneId,
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
        // organization, so it is a query parameter exactly like the entity deletion and the other workspace
        // by-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or scene id can never address a stored row; treat each as hidden (404),
        // never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenScene();
        }

        if (!Guid.TryParse(sceneId, out var sceneGuid) || sceneGuid == Guid.Empty)
        {
            return HiddenScene();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing scene (threat T5).
            return HiddenScene();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace (the parent workspace,
        // resolved first). A caller who is a member of the tenant but NOT of the workspace must not learn the
        // workspace's scenes exist, so a missing membership is hidden as 404 (not 403) — the same rule as the
        // create route and the entity deletion route (threats T1/T5). The member's workspace role then drives
        // the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenScene();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to know
        // the workspace exists, but not to delete its scenes). Scenes are host-prepared content, governed by
        // the host-capable roles Owner/Admin/Host/CoHost (csv/api_routes.csv; docs/06_AUTHORIZATION_MATRIX.md),
        // the same set that creates scenes/content blocks and deletes entities/content blocks. MembershipRole
        // is non-linear, so this is an EXACT set membership check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized: delete the scene (cascade its child content blocks + dependents, re-pack the remaining
        // scenes' ordering, append the audit record) atomically. The service loads the scene through the
        // tenant- AND workspace-scoped FindByIdAsync, so a scene in another workspace or tenant is never
        // deleted even when its id is known; an unknown id is a SAFE 404 that changes nothing (threats T1/T5).
        var now = timeProvider.GetUtcNow();
        var result = await deps.SceneDeletion
            .DeleteAsync(context.OrganizationId, workspaceGuid, sceneGuid, context.UserProfileId, now, cancellationToken)
            .ConfigureAwait(false);

        return result == SceneDeletionResult.Deleted
            ? Results.NoContent()
            : HiddenScene();
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist
    /// only when a database connection string is configured; when absent, the endpoint
    /// fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out SceneEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var scenes = services.GetService<ISceneRepository>();
        var sceneDeletion = services.GetService<SceneDeletionService>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || scenes is null
            || sceneDeletion is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new SceneEndpointDependencies(resolver, scenes, sceneDeletion, workspaceMembers);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields
    /// <see langword="false"/> (the caller returns 401). The mapping error reason is
    /// never echoed to the response (threat T7).
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
            detail: "Scene operations require persistence, which is not configured.");

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
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // Workspace existence is hidden: a malformed id, a workspace in a foreign or
    // non-entitled tenant, an unknown workspace, and a workspace the caller does not
    // belong to are ALL reported as 404, never distinguishable from each other and never
    // echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenWorkspace() => NotFound();

    // Scene existence is hidden (the by-scene-id read, CORE-API-007): a malformed id, a
    // scene in a foreign or non-entitled tenant, an unknown scene, and a scene in a
    // workspace the caller does not belong to are ALL reported as 404, never
    // distinguishable from each other and never echoing the reason (docs/08; threats
    // T1/T5).
    private static IResult HiddenScene() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SceneEndpointDependencies(
        TenantContextResolver Resolver,
        ISceneRepository Scenes,
        SceneDeletionService SceneDeletion,
        IWorkspaceMemberRepository WorkspaceMembers);
}
