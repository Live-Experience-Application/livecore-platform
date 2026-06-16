using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Entities;

/// <summary>
/// HTTP endpoints of the Entities module's generic ENTITY-TYPE routes (CORE-ENT-007, "Add the entity-type
/// create list and read endpoints", the "Vertical Authoring and Read API Completeness" epic). An entity type is
/// the DATA-DRIVEN definition of a kind of entity (its template key plus field/type metadata) through which a
/// vertical maps its domain onto Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). Until now the
/// entity-type model/repository (CORE-ENT-001) and the template loader (CORE-ENT-004) were implemented with NO
/// HTTP route (the deliberately-absent capability of docs/24); these routes add the authoring CRUD so an
/// authoring role can define, list and read its types over the API. They realize the documented request flow
/// end-to-end — "authentication middleware -> tenant/workspace context resolver -> endpoint -> authorization
/// policy" (docs/02_ARCHITECTURE.md) — mirroring <see cref="EntityEndpoints"/>'s workspace-scoped routes.
///
/// Routes owned here, all under <c>/api/v1/workspaces/{workspaceId}/entity-types</c> (csv/api_routes.csv), all
/// roles "Host,CoHost,Owner,Admin":
/// <list type="bullet">
///   <item><c>GET  /api/v1/workspaces/{workspaceId}/entity-types</c> — lists the workspace's entity types in
///   the deterministic (canonical type-key) order via <see cref="IEntityTypeRepository.ListByWorkspaceAsync"/>,
///   projected to <see cref="EntityTypeResponse"/>.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/entity-types</c> — defines a generic entity type via
///   <see cref="EntityTypeCreationService"/>: the type is created with a SERVER-MINTED surrogate id and the
///   creation is appended to the append-only audit log (<see cref="Audit.AuditAction.EntityTypeCreated"/>) — the
///   insert and the audit committing together in one transaction. Returns <c>201 Created</c> with the
///   <see cref="EntityTypeResponse"/>. A key the workspace already defines is a <c>409 Conflict</c>; an archived
///   (read-only) workspace rejects the create with a <c>409</c> (CORE-LIFE-009).</item>
///   <item><c>GET  /api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}</c> — reads ONE entity type
///   within the route's workspace via <see cref="IEntityTypeRepository.FindByIdAsync"/> (tenant- AND
///   workspace-scoped). A foreign/unknown type is a hidden <c>404</c>.</item>
/// </list>
///
/// Tenant resolution (mirrors the entity routes): the route path carries the <c>{workspaceId}</c>, so the
/// target organization is supplied by a required <c>organizationSlug</c> value (a query parameter for the GET
/// reads, a body field for the POST create) and turned into a trusted <see cref="TenantContext"/> by
/// <see cref="TenantContextResolver"/> (token organization claim AND persisted organization membership — defence
/// in depth, threat T5). The caller's WORKSPACE membership in the route's workspace is then loaded and the
/// operation is authorized by that workspace role.
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5),
/// load-then-authorize, fail-closed at every step and never leaking why. An entity type is an AUTHORING/schema
/// artifact, not audience content, so — unlike the entity list/read which open to any workspace member and
/// project by role — ALL THREE entity-type routes are restricted to the authoring roles (the story:
/// "An authoring role can define, list and read … non-authoring roles cannot create"; "authorize like entity
/// authoring"). There is no host-vs-participant projection because no audience role ever receives an entity
/// type:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed
///   mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed workspace id or entity-type id, and a
///   denied tenant resolution, are hidden as 404 (a caller who cannot see the tenant must not learn whether the
///   type exists; threat T5).</item>
///   <item>A caller who is not a member of the route's workspace is hidden as 404 (a non-member must not learn
///   the workspace's types exist; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the authoring role is 403 (authorized to know the workspace
///   exists, but not to manage/view its type definitions). The authoring role set is
///   <c>Owner</c>/<c>Admin</c>/<c>Host</c>/<c>CoHost</c> (csv/api_routes.csv; the same host-capable set that
///   creates entities, scenes and content blocks). <see cref="MembershipRole"/> is non-linear, so the check is
///   EXACT set membership, never an ordering comparison.</item>
/// </list>
///
/// Persistence dependency: like the entity endpoints, these use the repositories, the create service and the
/// tenant context resolver, which are registered only when a database connection string is configured (see
/// <c>Program.cs</c>). When persistence is off the endpoint fails closed with 503 rather than crashing startup.
/// </summary>
internal static class EntityTypeEndpoints
{
    /// <summary>Required value naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapEntityTypeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapGet("/{workspaceId}/entity-types", ListEntityTypesAsync);
        workspaceScopedGroup.MapPost("/{workspaceId}/entity-types", CreateEntityTypeAsync);
        workspaceScopedGroup.MapGet("/{workspaceId}/entity-types/{entityTypeId}", GetEntityTypeByIdAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces/{workspaceId}/entity-types?organizationSlug={slug}
    private static async Task<IResult> ListEntityTypesAsync(
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntityType();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (threat T5).
            return HiddenEntityType();
        }

        var context = resolution.Context;

        var authorization = await TryAuthorizeAuthoringMemberAsync(deps, context, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        // The entity types are returned in the deterministic (canonical type-key) order the repository enforces.
        var entityTypes = await deps.EntityTypes
            .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(entityTypes.Select(EntityTypeResponse.From).ToArray());
    }

    // GET /api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}?organizationSlug={slug}
    private static async Task<IResult> GetEntityTypeByIdAsync(
        HttpContext httpContext,
        string workspaceId,
        string entityTypeId,
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

        // A malformed workspace or entity-type id can never address a stored row; treat each as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntityType();
        }

        if (!Guid.TryParse(entityTypeId, out var entityTypeGuid) || entityTypeGuid == Guid.Empty)
        {
            return HiddenEntityType();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (threat T5).
            return HiddenEntityType();
        }

        var context = resolution.Context;

        var authorization = await TryAuthorizeAuthoringMemberAsync(deps, context, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        // Load the entity type through the tenant- AND workspace-scoped FindByIdAsync (it leads with
        // organization_id then workspace_id then the type id), so a type in another workspace or tenant is never
        // returned even when the surrogate id matches; an unknown id is a hidden 404 (threats T1/T5).
        var entityType = await deps.EntityTypes
            .FindByIdAsync(context.OrganizationId, workspaceGuid, entityTypeGuid, cancellationToken)
            .ConfigureAwait(false);
        if (entityType is null)
        {
            return HiddenEntityType();
        }

        return Results.Ok(EntityTypeResponse.From(entityType));
    }

    // POST /api/v1/workspaces/{workspaceId}/entity-types
    private static async Task<IResult> CreateEntityTypeAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateEntityTypeRequest? request,
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

        // Validate the entity-type inputs before touching the tenant, so a broken body is a 400 regardless of
        // authorization. The key, display name and attribute schema are template-/host-supplied DATA validated
        // for SHAPE only (the template boundary, docs/04); the rejected content is never echoed back (threat T7).
        // The key is canonicalized (trim + lower-case) and checked against the canonical slug shape; the
        // attribute schema defaults to an empty JSON object when omitted, so defining a type with no attributes
        // yet needs no body field.
        var typeKey = request.TypeKey?.Trim().ToLowerInvariant();
        if (!EntityType.IsValidTypeKey(typeKey))
        {
            return ValidationError("A valid entity type key is required.");
        }

        var displayName = request.DisplayName?.Trim();
        if (!EntityType.IsValidDisplayName(displayName))
        {
            return ValidationError("A valid entity type display name is required.");
        }

        var attributeSchema = string.IsNullOrWhiteSpace(request.AttributeSchema)
            ? "{}"
            : request.AttributeSchema.Trim();
        if (!EntityType.IsValidAttributeSchema(attributeSchema))
        {
            return ValidationError("A valid entity type attribute schema is required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404).
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenEntityType();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 (threat T5).
            return HiddenEntityType();
        }

        var context = resolution.Context;

        var authorization = await TryAuthorizeAuthoringMemberAsync(deps, context, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.Result is not null)
        {
            return authorization.Result;
        }

        // Read-only when the parent workspace is archived (CORE-LIFE-009): defining an entity type is an
        // authoring mutation, so an archived workspace rejects it with a 409 Conflict that creates nothing. The
        // workspace is loaded within the resolved tenant (the membership above already proves it exists, so a
        // null here is defensive and hidden as 404); the check is placed AFTER role authorization so a member
        // who lacks the authoring role still gets a 403 and never learns the archived state (threat T7),
        // mirroring the entity create handler.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenEntityType();
        }

        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Authorized: define the entity type (persisting it and appending the EntityTypeCreated audit record)
        // atomically. The service resolves any existing key within the workspace first, so a key the workspace
        // already defines is a 409 Conflict that creates nothing; the unique per-workspace key index remains the
        // race guard.
        var now = timeProvider.GetUtcNow();
        var result = await deps.EntityTypeCreation
            .CreateAsync(
                context.OrganizationId,
                workspaceGuid,
                typeKey!,
                displayName!,
                attributeSchema,
                context.UserProfileId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Status == EntityTypeCreationStatus.DuplicateKey)
        {
            return DuplicateKey();
        }

        var entityType = result.EntityType!;
        var response = EntityTypeResponse.From(entityType);
        return Results.Created(
            $"/api/v1/workspaces/{workspaceGuid}/entity-types/{entityType.Id}", response);
    }

    /// <summary>
    /// Object-level authorization shared by all three routes: the caller must be a member of THIS workspace AND
    /// hold an authoring role. A non-member is hidden as 404 (membership-before-role, never leaking existence;
    /// threats T1/T5); a known member who lacks the authoring role is 403 (authorized to know the workspace
    /// exists, but not to manage/view its type definitions). The authoring role set is Owner/Admin/Host/CoHost
    /// (csv/api_routes.csv); <see cref="MembershipRole"/> is non-linear, so this is an EXACT set-membership
    /// check, never an ordering comparison. Returns a non-null <see cref="AuthorizationOutcome.Result"/> (the
    /// error response) when the caller is denied, or a null result when the caller is an authorized authoring
    /// member.
    /// </summary>
    private static async Task<AuthorizationOutcome> TryAuthorizeAuthoringMemberAsync(
        EntityTypeEndpointDependencies deps,
        TenantContext context,
        Guid workspaceGuid,
        CancellationToken cancellationToken)
    {
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return new AuthorizationOutcome(HiddenEntityType());
        }

        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return new AuthorizationOutcome(Forbidden());
        }

        return new AuthorizationOutcome(null);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out EntityTypeEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var entityTypes = services.GetService<IEntityTypeRepository>();
        var entityTypeCreation = services.GetService<EntityTypeCreationService>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || entityTypes is null
            || entityTypeCreation is null
            || workspaces is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new EntityTypeEndpointDependencies(
            resolver, entityTypes, entityTypeCreation, workspaces, workspaceMembers);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an <see cref="OidcPrincipal"/>, fail-closed: a
    /// failed mapping yields <see langword="false"/> (the caller returns 401). The mapping error reason is never
    /// echoed to the response (threat T7).
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
            detail: "Entity type operations require persistence, which is not configured.");

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

    // The parent workspace is archived and therefore read-only (CORE-LIFE-009), so defining an entity type in it
    // is refused. The caller is authorized; the workspace's lifecycle state, not the caller, is the reason, so
    // this is a 409 Conflict. The detail names only the generic state and leaks no tenant data (threat T7).
    private static IResult ArchivedReadOnly()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.WorkspaceArchived,
            title: "Conflict",
            detail: "The workspace is archived and is read-only.");

    // The workspace already defines a type with this key (the per-workspace unique natural key). The caller is
    // authorized; the existing definition, not the caller, is the reason, so this is a 409 Conflict. The detail
    // names only the generic condition and leaks no tenant data (threat T7).
    private static IResult DuplicateKey()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.DuplicateResource,
            title: "Conflict",
            detail: "An entity type with this key already exists in the workspace.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

    // Entity-type existence is hidden: a malformed workspace/type id, a type in a foreign or non-entitled
    // tenant, a type in another workspace, an unknown type, and a type in a workspace the caller does not belong
    // to are ALL reported as 404, never distinguishable from each other and never echoing the reason (docs/08;
    // threats T1/T5).
    private static IResult HiddenEntityType()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    /// <summary>
    /// The result of the shared authorization step: a non-null <see cref="Result"/> is the error response to
    /// return (a hidden 404 for a non-member, a 403 for a member without the authoring role); a null result
    /// means the caller is an authorized authoring member and the handler may proceed.
    /// </summary>
    private readonly record struct AuthorizationOutcome(IResult? Result);

    private readonly record struct EntityTypeEndpointDependencies(
        TenantContextResolver Resolver,
        IEntityTypeRepository EntityTypes,
        EntityTypeCreationService EntityTypeCreation,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers);
}
