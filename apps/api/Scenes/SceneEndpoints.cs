using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Scenes;

/// <summary>
/// HTTP endpoints of the Scenes module's scene content APIs (CORE-SCENE-003:
/// "Implement scene content APIs"). These are the FIRST HTTP endpoints of the Scenes
/// module. They realize the documented request flow end-to-end for the two
/// workspace-scoped scene routes: "authentication middleware -> tenant/workspace
/// context resolver -> endpoint -> authorization policy"
/// (docs/02_ARCHITECTURE.md), mirroring <see cref="Workspaces.WorkspaceEndpoints"/>'s
/// <c>{workspaceId}</c>-in-path routes exactly.
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
        var group = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        group.MapGet("/{workspaceId}/scenes", ListScenesAsync);
        group.MapPost("/{workspaceId}/scenes", CreateSceneAsync);

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
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || scenes is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new SceneEndpointDependencies(resolver, scenes, workspaceMembers);
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

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SceneEndpointDependencies(
        TenantContextResolver Resolver,
        ISceneRepository Scenes,
        IWorkspaceMemberRepository WorkspaceMembers);
}
