using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Content;

/// <summary>
/// HTTP endpoint of the Content module's scene content APIs (CORE-SCENE-003:
/// "Implement scene content APIs"). This is the FIRST HTTP endpoint of the Content
/// module. It realizes the documented request flow end-to-end for the by-scene-id
/// content-block create route: "authentication middleware -> tenant/workspace context
/// resolver -> endpoint -> authorization policy" (docs/02_ARCHITECTURE.md), mirroring
/// <see cref="Sessions.SessionEndpoints"/>'s by-session-id routes exactly.
///
/// Route owned by this story (csv/api_routes.csv line 17):
/// <list type="bullet">
///   <item><c>POST /api/v1/scenes/{sceneId}/content-blocks</c> — module Content, roles
///   "Host,CoHost,Owner,Admin". Creates a content block in the route's scene via
///   <see cref="ContentBlock.Create"/> + <see cref="IContentBlockRepository.AddAsync"/>
///   at the initial revision (1). The request body carries the generic content
///   <see cref="ContentBlockType"/> (Text/Media/Data) and body.</item>
/// </list>
///
/// There is deliberately NO content-block list/get/update/revise endpoint here: no such
/// route exists in csv/api_routes.csv, so they are out of scope (the revise capability
/// lives on the aggregate for a later story).
///
/// Tenant resolution (mirrors the session by-id routes): the route path carries only
/// <c>{sceneId}</c>, so the target organization is supplied by a required
/// <c>organizationSlug</c> QUERY parameter and turned into a trusted
/// <see cref="TenantContext"/> by <see cref="TenantContextResolver"/> (token
/// organization claim AND persisted organization membership — defence in depth, threat
/// T5). The scene is then loaded WITHIN that resolved organization via
/// <see cref="ISceneRepository.FindByIdInOrganizationAsync"/> (the predicate leads with
/// the organization id, so a foreign-tenant scene is never found), the scene's own
/// workspace id is discovered from the loaded row AFTER the tenant boundary has been
/// enforced, and the caller's WORKSPACE membership in the SCENE'S own workspace is
/// loaded and authorized by that workspace role.
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5), load-then-authorize, fail-closed at every step and never leaking why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's
///   <see cref="ClaimsPrincipal"/>; a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed scene id and a
///   denied tenant resolution are hidden as 404 (a caller who cannot see the tenant must
///   not learn whether the scene exists; threat T5).</item>
///   <item>A scene not present in the resolved tenant is hidden as 404; a caller who is
///   not a member of the scene's workspace is ALSO hidden as 404 (a non-member must not
///   learn the scene exists — the same object-level rule as the session start/end
///   routes; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the content-create role is 403.
///   <see cref="MembershipRole"/> is non-linear, so the role check is EXACT (Host,
///   CoHost, Owner or Admin), never an ordering comparison.</item>
/// </list>
///
/// Persistence dependency: like the session endpoints, this uses the repositories and
/// the tenant context resolver, which are registered only when a database connection
/// string is configured (see <c>Program.cs</c>). When persistence is off the endpoint
/// fails closed with 503 rather than crashing startup.
/// </summary>
internal static class ContentBlockEndpoints
{
    /// <summary>Required query parameter naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapContentBlockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a
        // missing/invalid token is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/scenes")
            .RequireAuthorization();

        group.MapPost("/{sceneId}/content-blocks", CreateContentBlockAsync);

        return endpoints;
    }

    // POST /api/v1/scenes/{sceneId}/content-blocks?organizationSlug={slug}
    private static async Task<IResult> CreateContentBlockAsync(
        HttpContext httpContext,
        string sceneId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        [FromBody] CreateContentBlockRequest? request,
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

        // The target organization is required and supplied by the request; the route
        // path carries no organization, so it is a query parameter exactly like the
        // session by-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // Validate the content inputs before touching the tenant, so a broken body is a
        // 400 regardless of authorization. The type must be a DEFINED generic content
        // type, never an undefined value a cast could smuggle in; the body must satisfy
        // the aggregate's body invariants (rich validation/size limits are CORE-SCENE-005).
        if (!TryParseType(request.Type, out var type))
        {
            return ValidationError("A valid content block type is required.");
        }

        if (!ContentBlock.IsValidBody(request.Body?.Trim()))
        {
            return ValidationError("A valid content block body is required.");
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
        // AFTER the tenant boundary has been enforced.
        var scene = await deps.Scenes
            .FindByIdInOrganizationAsync(context.OrganizationId, sceneGuid, cancellationToken)
            .ConfigureAwait(false);
        if (scene is null)
        {
            return HiddenScene();
        }

        // Object-level authorization: the caller must be a member of the SCENE'S
        // workspace. A caller who is a member of the tenant but NOT of the scene's
        // workspace must not learn the scene exists, so a missing membership is hidden as
        // 404 (not 403) — the same rule as the session start/end routes (threats T1/T5).
        // The lookup is scoped by organization id then the scene's own workspace id, so a
        // control role held in a DIFFERENT workspace never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, scene.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenScene();
        }

        // The caller is a known member of the scene's workspace, so an insufficient role
        // is a 403. "Create content block" is "Host,CoHost,Owner,Admin"
        // (csv/api_routes.csv). MembershipRole is non-linear, so this is an EXACT set
        // membership check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Create the content block in the scene's own workspace at the initial revision
        // (1). The tenant, workspace and scene ids all come from the loaded scene row, so
        // the block is bound to exactly the scene addressed inside the enforced tenant
        // boundary (threat T5). A content block has no uniqueness constraint, so there is
        // no 409 outcome to translate; AddAsync always returns Added on success.
        var now = timeProvider.GetUtcNow();
        var contentBlock = ContentBlock.Create(
            scene.OrganizationId,
            scene.WorkspaceId,
            scene.Id,
            type,
            request.Body!.Trim(),
            now);

        await deps.ContentBlocks.AddAsync(contentBlock, cancellationToken).ConfigureAwait(false);

        var response = ContentBlockResponse.From(contentBlock);
        return Results.Created(
            $"/api/v1/scenes/{scene.Id}/content-blocks/{contentBlock.Id}",
            response);
    }

    /// <summary>
    /// Parses a generic content block type name (case-insensitive) and confirms it is a
    /// DEFINED type. A null, blank, numeric, unknown or undefined value is rejected so a
    /// caller cannot smuggle an undefined enum value past the defined-check via
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>'s numeric path
    /// (mirrors <c>WorkspaceEndpoints.TryParseRole</c>). The type is matched by its stable
    /// NAME only; it is never interpreted into a capability here.
    /// </summary>
    private static bool TryParseType(string? value, out ContentBlockType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Reject purely numeric input: only the stable type NAMES are accepted, so a
        // caller cannot smuggle an out-of-range numeric value past the defined-check.
        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out type)
            && ContentBlock.IsValidType(type);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist
    /// only when a database connection string is configured; when absent, the endpoint
    /// fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out ContentBlockEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var scenes = services.GetService<ISceneRepository>();
        var contentBlocks = services.GetService<IContentBlockRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null
            || scenes is null
            || contentBlocks is null
            || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new ContentBlockEndpointDependencies(resolver, scenes, contentBlocks, workspaceMembers);
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
            detail: "Content block operations require persistence, which is not configured.");

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

    // Scene existence is hidden: a malformed id, a scene in a foreign or non-entitled
    // tenant, an unknown scene, and a scene in a workspace the caller does not belong to
    // are ALL reported as 404, never distinguishable from each other and never echoing
    // the reason (docs/08; threats T1/T5).
    private static IResult HiddenScene() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct ContentBlockEndpointDependencies(
        TenantContextResolver Resolver,
        ISceneRepository Scenes,
        IContentBlockRepository ContentBlocks,
        IWorkspaceMemberRepository WorkspaceMembers);
}
