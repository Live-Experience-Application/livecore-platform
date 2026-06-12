using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Assets;

/// <summary>
/// HTTP endpoint of the Assets module's upload-intent flow (CORE-AST-003: "Implement upload intent flow").
/// This is the Assets module's FIRST HTTP route. It realizes the documented request flow
/// (authentication -&gt; tenant/workspace context resolver -&gt; endpoint -&gt; authorization policy -&gt;
/// command, docs/02_ARCHITECTURE.md), mirroring <see cref="LiveCore.Api.Visibility.RevealEndpoints"/> and
/// <see cref="LiveCore.Api.Scenes.SceneEndpoints"/>.
///
/// Route owned by this story (csv/api_routes.csv line 19):
/// <list type="bullet">
///   <item><c>POST /api/v1/assets/upload-intent</c> — module <b>Assets</b>, roles
///   "Host,CoHost,Owner,Admin", "Creates upload intent".</item>
/// </list>
///
/// Authoritative behavior — the command registers a new <see cref="AssetStatus.Pending"/>
/// <see cref="Asset"/> with SERVER-MINTED storage coordinates and returns the short-lived, signed upload
/// URL the client uploads the object with (<see cref="AssetUploadIntentService"/>, reusing the CORE-AST-001
/// aggregate and the CORE-AST-002 <see cref="IAssetStorage"/> adapter port). The asset is PRIVATE by
/// default: the only access handed out is one short-lived signed upload URL after the permission check
/// passes (the epic acceptance criterion: "Assets are private by default and accessed only through
/// authorized signed URLs"; threat T4 "Asset leak"). The client never supplies the bucket or object key,
/// so it can never point an upload at another tenant's or workspace's object (threats T5/T1).
///
/// Tenant resolution + authorization mirror the workspace create command. The route has no path
/// parameters, so the target organization is the body's <c>organizationSlug</c> resolved by
/// <see cref="TenantContextResolver"/> (token claim AND persisted membership, threat T5) and the workspace
/// is the body's <c>workspaceId</c>; the caller is authorized by their role in THAT workspace. Fail-closed
/// at every step and never leaking why:
/// <list type="bullet">
///   <item>503 when persistence is off; 401 when the principal cannot be mapped.</item>
///   <item>A malformed body or a missing <c>organizationSlug</c> is 400; an empty/malformed
///   <c>workspaceId</c>, a denied tenant resolution, and a caller who is not a member of the target
///   workspace are ALL hidden as 404 (never distinguishable, never 403 for a non-member; threats
///   T1/T5).</item>
///   <item>A known workspace member who lacks the upload role (Owner/Admin/Host/CoHost — the "Send private
///   content" / asset-write capability of docs/06_AUTHORIZATION_MATRIX.md, same role set as
///   csv/api_routes.csv) is 403. <see cref="MembershipRole"/> is non-linear, so the role check is EXACT,
///   never an ordering comparison.</item>
///   <item>Only AFTER authorization is the rest of the request validated (so an unauthorized caller never
///   receives request-shape feedback): a missing or invalid <c>contentType</c> is 400.</item>
///   <item>503 when no object storage is configured: the fail-closed <see cref="UnconfiguredAssetStorage"/>
///   throws <see cref="AssetStorageNotConfiguredException"/> and NO asset is persisted (private-by-default
///   holds even unconfigured), mirroring how the host denies cleanly without a database or OIDC authority.</item>
/// </list>
///
/// Persistence dependency: like the reveal/scene endpoints, this uses the repositories, the tenant context
/// resolver and the upload-intent service, which are registered only when a database connection string is
/// configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503.
/// </summary>
internal static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/assets")
            .RequireAuthorization();

        group.MapPost("/upload-intent", CreateUploadIntentAsync);

        return endpoints;
    }

    // POST /api/v1/assets/upload-intent
    private static async Task<IResult> CreateUploadIntentAsync(
        HttpContext httpContext,
        [FromBody] CreateUploadIntentRequest? request,
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

        // A missing/unparseable body cannot carry the target organization or workspace; 400. (Malformed
        // JSON is rejected as 400 by the framework before the handler.)
        if (request is null)
        {
            return ValidationError("A request body is required.");
        }

        // The target organization is required to resolve the tenant; it is supplied in the body (this
        // route has no path parameters).
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty workspace id can never address a stored workspace; hidden as 404.
        if (request.WorkspaceId == Guid.Empty)
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

        // Object-level authorization: the caller must be a member of the TARGET workspace. A caller who is
        // a member of the tenant but NOT of the workspace must not learn the workspace exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the workspace read-one route (threats
        // T1/T5). The member's workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, request.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The caller is a known member of the workspace, so an insufficient role is 403. The upload roles
        // are Owner/Admin/Host/CoHost (csv/api_routes.csv "Host,CoHost,Owner,Admin"; the asset-write
        // capability of docs/06_AUTHORIZATION_MATRIX.md). MembershipRole is non-linear, so this is an EXACT
        // set membership check, never an ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Authorized. Only now validate the rest of the request, so an unauthorized caller never receives
        // request-shape feedback.
        if (!Asset.IsValidContentType(request.ContentType?.Trim()))
        {
            return ValidationError("A valid content type is required.");
        }

        var now = timeProvider.GetUtcNow();

        AssetUploadIntent intent;
        try
        {
            intent = await deps.UploadIntents
                .CreateAsync(
                    context.OrganizationId,
                    request.WorkspaceId,
                    context.UserProfileId,
                    request.ContentType!.Trim(),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AssetStorageNotConfiguredException)
        {
            // No object storage is configured for this deployment: fail closed. The command minted no URL
            // and persisted no asset, so the private-by-default posture holds (threat T4). The response
            // never leaks any storage coordinate (the exception message carries only the operation name).
            return StorageUnavailable();
        }

        // The created resource is reached through its (later) signed download route; point the Location
        // there (csv/api_routes.csv GET /api/v1/assets/{assetId}/download-url, CORE-AST-004).
        var response = UploadIntentResponse.From(intent.Asset, intent.UploadUrl);
        return Results.Created($"/api/v1/assets/{intent.Asset.Id}/download-url", response);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out AssetEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var uploadIntents = services.GetService<AssetUploadIntentService>();

        if (resolver is null
            || workspaceMembers is null
            || uploadIntents is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new AssetEndpointDependencies(resolver, workspaceMembers, uploadIntents);
        return true;
    }

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
            detail: "Asset operations require persistence, which is not configured.");

    private static IResult StorageUnavailable()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Asset storage is not configured.");

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
        => ValidationError("The 'organizationSlug' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // Workspace existence is hidden: an empty/malformed workspace id, a workspace in a foreign or
    // non-entitled tenant, and a workspace the caller does not belong to are ALL reported as 404, never
    // distinguishable and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenWorkspace() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct AssetEndpointDependencies(
        TenantContextResolver Resolver,
        IWorkspaceMemberRepository WorkspaceMembers,
        AssetUploadIntentService UploadIntents);
}
