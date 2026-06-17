// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.SystemModule;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Scenes;

/// <summary>
/// HTTP endpoints of the Scenes module. The scene content read/create routes are CORE-SCENE-003
/// ("Implement scene content APIs") and the by-scene-id read is CORE-API-007; the scene DELETE route is
/// CORE-LIFE-005 ("Implement scene deletion", the "Resource Lifecycle and Deletion" epic); the scene REORDER
/// route is CORE-SCENE-006 ("Implement the scene reorder endpoint", the "Authoring Completeness" epic). They
/// realize the
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
///   The user-driven reorder is the separate <c>.../reorder</c> route below (CORE-SCENE-006).
///   Rejected with 409 when the parent workspace is archived (read-only;
///   CORE-LIFE-009).</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder</c> — module
///   Scenes, roles "Host,CoHost,Owner,Admin" (CORE-SCENE-006). Moves a scene to a desired
///   0-based position via <see cref="SceneReorderService"/>, which re-packs the workspace's
///   scene ordering DETERMINISTICALLY (contiguous, gap-free, no duplicates) reusing the
///   SCENE-001 (scene_order, id) ordering and the delete-repack logic. The position is a
///   client-supplied LIST INDEX, but the actual order is assigned SERVER-SIDE (no
///   client-trusted absolute order); a too-large index clamps to the end and a negative index
///   is a 400. A reorder is a benign metadata move, so it is NOT audited. Concurrent reorders
///   are made safe by the scene's xmin token (CORE-CONC-006): the loser gets a 409 rather than
///   corrupting the order. Rejected with 409 when the parent workspace is archived
///   (CORE-LIFE-009). Returns the re-packed scene list; a foreign/unknown scene is hidden-404.</item>
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
        workspaceScopedGroup.MapPost("/{workspaceId}/scenes/{sceneId}/reorder", ReorderSceneAsync);
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

    // GET /api/v1/workspaces/{workspaceId}/scenes?organizationSlug={slug}&limit={n}&offset={n}
    private static async Task<IResult> ListScenesAsync(
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

        // The scenes are returned in the deterministic (scene_order, id) order the
        // repository enforces. The list is then PROJECTED BY ROLE (CORE-SCENE-004,
        // csv/api_routes.csv line 15 "Projection by role"): host-capable and
        // metadata-entitled roles (Owner/Admin/Host/CoHost/Auditor) receive the full
        // host shape (SceneResponse); the audience roles (Participant/Observer, whose
        // "View workspace metadata" is "limited" in docs/06_AUTHORIZATION_MATRIX.md)
        // receive the host-only-field-stripped participant shape
        // (ParticipantSceneResponse). Only the per-scene SHAPE changes by role — every
        // member still receives the SAME page of the workspace's scenes (deciding WHICH
        // scenes an audience may see is the Visibility module's later concern, not this
        // projection). MembershipRole is non-linear, so the classification is EXACT set
        // membership, never a >/< comparison (SceneProjection.ReceivesHostShape).
        //
        // BOUNDED (CORE-DX-003): the page is read through the tenant- and workspace-scoped paged repository,
        // fetching ONE extra row so HasMore is set without a second COUNT; the trimmed page is then
        // role-projected into the PageResponse envelope. A list can never return the whole table (threat T9).
        var rows = await deps.Scenes
            .ListPageByWorkspaceAsync(context.OrganizationId, workspaceGuid, pageOffset, pageLimit + 1, cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > pageLimit;
        var pageRows = hasMore ? rows.Take(pageLimit).ToArray() : rows;

        return Results.Ok(SceneProjection.ProjectPage(pageRows, member.Role, pageOffset, pageLimit, hasMore));
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

        // Read-only when the parent workspace is archived (CORE-LIFE-009): creating a scene is an authoring
        // mutation, so an archived workspace rejects it with a 409 Conflict that creates nothing. The workspace
        // is loaded within the resolved tenant (the membership above already proves it exists, so a null here is
        // defensive and hidden as 404); the check is placed AFTER role authorization so a member who lacks the
        // create role still gets a 403 and never learns the archived state (threat T7).
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Honor an OPTIONAL client Idempotency-Key (CORE-DX-004): a create/network retry under the same key
        // returns the ORIGINAL scene and creates exactly one. A malformed key is a 400, checked AFTER
        // authorization so an unauthorized caller never receives request-shape feedback (the reveal rule).
        if (IdempotencyKeyHeader.Read(httpContext, out var idempotencyKey) == IdempotencyKeyHeaderResult.Invalid)
        {
            return ValidationError($"The '{IdempotencyKeyHeader.HeaderName}' header is malformed.");
        }

        // Append-to-end ordering (CORE-SCENE-001 deferred this to the endpoint story):
        // the new scene's position is the next order after the current maximum in the
        // workspace. An empty workspace gets the first order (0). The order is assigned
        // server-side; the client never supplies one. The maximum is computed by the
        // database (MAX(scene_order)) rather than by materializing the whole scene list,
        // so the create can never load an unbounded number of rows just to find the next
        // position (CORE-DX-003: no unbounded load; threat T9; benefits CORE-PERF-001).
        var maxOrder = await deps.Scenes
            .GetMaxOrderByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        var nextOrder = maxOrder is { } currentMax ? currentMax + 1 : 0;

        var now = timeProvider.GetUtcNow();
        var scene = Scene.Create(context.OrganizationId, workspaceGuid, request.Title!.Trim(), nextOrder, now);

        // The create + the idempotency-key record commit atomically (CORE-DX-004); the scope is per-tenant and
        // per-operation. A scene has no uniqueness constraint, so AddAsync always returns Added on success (a
        // foreign-key violation would surface as a DbUpdateException, which the membership check above already
        // precludes for a resolved, existing workspace).
        var creation = await deps.Idempotency
            .CreateAsync(
                idempotencyKey,
                $"scene-create:{context.OrganizationId}",
                now,
                create: async transactionCancellationToken =>
                {
                    await deps.Scenes.AddAsync(scene, transactionCancellationToken).ConfigureAwait(false);
                    return scene;
                },
                resultIdSelector: created => created.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (creation.Replayed)
        {
            // A prior request with this key already created the scene; return the original (200), creating
            // nothing and never assigning a second order. The scene is re-loaded WITHIN the resolved tenant.
            var original = await deps.Scenes
                .FindByIdInOrganizationAsync(context.OrganizationId, creation.ReplayResultId, cancellationToken)
                .ConfigureAwait(false);
            return original is null
                ? HiddenScene()
                : Results.Ok(SceneResponse.From(original));
        }

        var response = SceneResponse.From(scene);
        return Results.Created($"/api/v1/scenes/{scene.Id}", response);
    }

    // POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder
    //
    // Moves ONE scene to a new position within its parent workspace (CORE-SCENE-006, the "Authoring
    // Completeness" epic). The flow mirrors the create/delete handlers (the same fail-closed,
    // load-then-authorize, hidden-404 shape): the parent workspace is resolved FIRST (the route pins
    // {workspaceId} and {sceneId}, the tenant comes from the required organizationSlug body field), the caller
    // is authorized by their role in that workspace, and only then is the scene moved — the workspace's scene
    // ordering re-packed deterministically (contiguous, gap-free, no duplicates) — atomically through the
    // tenant- and workspace-scoped reorder service. The target POSITION (a 0-based list index) is supplied by
    // the client, but the actual order is assigned SERVER-SIDE (no client-trusted absolute order). A reorder
    // is a benign metadata move, so it is NOT audited (unlike the delete). On success the re-packed scene list
    // is returned; reordering a non-existent or foreign scene is a safe hidden-404.
    private static async Task<IResult> ReorderSceneAsync(
        HttpContext httpContext,
        string workspaceId,
        string sceneId,
        [FromBody] ReorderSceneRequest? request,
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

        // The target position is a 0-based list index; a negative index is malformed input and is rejected as
        // a 400 before touching the tenant (an out-of-range/foreign SCENE is hidden as 404; a too-large index
        // is clamped to the end by the service). Validating the body shape first keeps a broken request a 400
        // regardless of authorization.
        if (request.TargetIndex < 0)
        {
            return ValidationError("A non-negative target index is required.");
        }

        // A malformed workspace or scene id can never address a stored row; treat each as hidden (404), never
        // echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenScene();
        }

        if (!Guid.TryParse(sceneId, out var sceneGuid) || sceneGuid == Guid.Empty)
        {
            return HiddenScene();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is indistinguishable
            // from a missing scene (threat T5).
            return HiddenScene();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace (the parent workspace,
        // resolved first). A caller who is a member of the tenant but NOT of the workspace must not learn the
        // workspace's scenes exist, so a missing membership is hidden as 404 (not 403) — the same rule as the
        // create and delete routes (threats T1/T5). The member's workspace role then drives the role check.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenScene();
        }

        // The caller is a known member of the workspace, so an insufficient role is a 403 (authorized to know
        // the workspace exists, but not to reorder its scenes). Reordering is host-prepared authoring, governed
        // by the host-capable roles Owner/Admin/Host/CoHost (csv/api_routes.csv; docs/06_AUTHORIZATION_MATRIX.md),
        // exactly like scene create and delete. MembershipRole is non-linear, so this is an EXACT set membership
        // check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Read-only when the parent workspace is archived (CORE-LIFE-009): reordering a scene is an authoring
        // mutation, so an archived workspace rejects it with a 409 Conflict that changes nothing. The check is
        // placed AFTER role authorization so a member who lacks the reorder role still gets a 403 and never
        // learns the archived state (threat T7), mirroring the scene create handler.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenScene();
        }

        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Authorized: move the scene to its target position and re-pack the workspace's scene ordering
        // atomically. The service loads the scene through the tenant- AND workspace-scoped FindByIdAsync, so a
        // scene in another workspace or tenant is never moved even when its id is known; an unknown id is a
        // SAFE 404 that changes nothing (threats T1/T5). A concurrent reorder is detected by the xmin token
        // and surfaces as a 409 through the ConcurrencyConflictMiddleware (CORE-CONC-006).
        var now = timeProvider.GetUtcNow();
        var result = await deps.SceneReorder
            .ReorderAsync(context.OrganizationId, workspaceGuid, sceneGuid, request.TargetIndex, now, cancellationToken)
            .ConfigureAwait(false);

        if (result != SceneReorderResult.Reordered)
        {
            return HiddenScene();
        }

        // Return the re-packed scene list so the caller sees the new (scene_order, id) sequence. The reorder
        // role is always host-capable (the audience roles were already 403'd), so the host shape is always the
        // right projection; reuse the same projector the list/by-id reads use for consistency.
        var scenes = await deps.Scenes
            .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(SceneProjection.Project(scenes, member.Role));
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
        var sceneReorder = services.GetService<SceneReorderService>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var idempotency = services.GetService<IdempotentResourceCreator>();

        if (resolver is null
            || scenes is null
            || sceneDeletion is null
            || sceneReorder is null
            || workspaces is null
            || workspaceMembers is null
            || idempotency is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new SceneEndpointDependencies(
            resolver, scenes, sceneDeletion, sceneReorder, workspaces, workspaceMembers, idempotency);
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Scene operations require persistence, which is not configured.");

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

    // The parent workspace is archived and therefore read-only (CORE-LIFE-009), so creating a scene in it is
    // refused. The caller is authorized; the workspace's lifecycle state, not the caller, is the reason, so this
    // is a 409 Conflict. The detail names only the generic state and leaks no tenant data (threat T7).
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
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SceneEndpointDependencies(
        TenantContextResolver Resolver,
        ISceneRepository Scenes,
        SceneDeletionService SceneDeletion,
        SceneReorderService SceneReorder,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers,
        IdempotentResourceCreator Idempotency);
}
