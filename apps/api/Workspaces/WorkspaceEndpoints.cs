using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// HTTP endpoints of the Workspaces module (CORE-WS-003: workspace
/// create/read/update API). This is the first endpoint story, so it realizes the
/// documented request flow end-to-end for the workspace routes:
/// "authentication middleware -> tenant/workspace context resolver -> endpoint
/// -> authorization policy" (docs/02_ARCHITECTURE.md).
///
/// Routes owned by this story (csv/api_routes.csv, plus the rename slice of the
/// "update" title; all other workspace routes are later stories):
/// <list type="bullet">
///   <item><c>GET  /api/v1/workspaces</c> — list, filtered to the caller's
///   memberships within the target organization (all membership roles).</item>
///   <item><c>POST /api/v1/workspaces</c> — create a generic workspace,
///   authorized by the caller's organization role (Owner or Admin).</item>
///   <item><c>GET  /api/v1/workspaces/{workspaceId}</c> — read, workspace
///   members only; non-member or cross-tenant is hidden as 404.</item>
///   <item><c>PUT  /api/v1/workspaces/{workspaceId}</c> — rename (the update
///   slice), "Manage workspace settings" (Owner or Admin).</item>
/// </list>
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5):
/// <list type="bullet">
///   <item>The authenticated principal is mapped fail-closed from the request's
///   <see cref="ClaimsPrincipal"/> to an <see cref="OidcPrincipal"/>; a failed
///   mapping is 401.</item>
///   <item>The target organization is supplied by the request (a body field for
///   writes, a required query parameter for reads) and turned into a trusted
///   <see cref="TenantContext"/> by <see cref="TenantContextResolver"/>, which
///   matches the token organization claim AND a persisted organization
///   membership. A failed resolution is hidden as 404 for org-scoped reads
///   (so a foreign or non-existent tenant is indistinguishable) and reported as
///   403 for writes when the caller is authenticated but lacks standing.</item>
///   <item><c>MembershipRole</c> is non-linear, so role checks are EXACT
///   (role == Owner || role == Admin), never an ordering comparison.</item>
///   <item>Per-action authorization is inline here (CORE-WS-005 builds the reusable
///   policy/handler framework; it is deliberately not built in this story).</item>
/// </list>
///
/// Persistence dependency: the endpoints use the repositories and the tenant
/// context resolver, which are registered only when a database connection string
/// is configured (see <c>Program.cs</c>). When persistence is off the endpoints
/// fail closed with 503 (Service Unavailable) rather than crashing startup,
/// keeping the existing conditional pattern and the smoke tests green.
/// </summary>
internal static class WorkspaceEndpoints
{
    /// <summary>Required query parameter naming the target organization for reads.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        // The health endpoints stay anonymous because they are mapped outside
        // this group.
        var group = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        group.MapGet("/", ListWorkspacesAsync);
        group.MapPost("/", CreateWorkspaceAsync);
        group.MapGet("/{workspaceId}", GetWorkspaceAsync);
        group.MapPut("/{workspaceId}", UpdateWorkspaceAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces?organizationSlug={slug}
    private static async Task<IResult> ListWorkspacesAsync(
        HttpContext httpContext,
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

        // The target organization is required and supplied by the request. We do
        // not silently pick "the caller's only organization".
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // List is allowed to ANY membership role, so a denied resolution (no
        // claim, no membership, unknown/foreign tenant) is simply an empty,
        // hidden result: a non-entitled caller learns nothing about the tenant
        // (threat T5). We return 404 to keep tenant existence hidden, mirroring
        // the read-by-id rule.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return HiddenOrganization();
        }

        var context = resolution.Context;
        var workspaces = await deps.Workspaces
            .ListByMemberAsync(context.OrganizationId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);

        var response = workspaces.Select(WorkspaceResponse.From).ToArray();
        return Results.Ok(response);
    }

    // POST /api/v1/workspaces
    private static async Task<IResult> CreateWorkspaceAsync(
        HttpContext httpContext,
        [FromBody] CreateWorkspaceRequest? request,
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

        // Validate the workspace inputs before touching the tenant, so a broken
        // body is a 400 regardless of authorization.
        if (!Workspace.IsValidName(request.Name?.Trim()))
        {
            return ValidationError("A valid workspace name is required.");
        }

        string canonicalSlug;
        try
        {
            canonicalSlug = Workspace.CanonicalizeSlug(request.Slug);
        }
        catch (ArgumentException)
        {
            return ValidationError("A valid workspace slug is required.");
        }

        // POST creates a NEW workspace, so there is no workspace membership to
        // authorize against: it is authorized by the caller's ORGANIZATION role.
        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // The caller is authenticated but is not entitled to the target
            // organization (no claim, no membership, or it does not exist).
            // Creating is a privileged write, so this is 403, not a hidden 404:
            // the caller already named the organization in the body.
            return Forbidden();
        }

        var context = resolution.Context;

        // Exact, non-linear role check: only an organization Owner or Admin may
        // create a workspace ("Creates generic workspace"; csv/api_routes.csv
        // roles "Owner,Admin"; docs/06_AUTHORIZATION_MATRIX.md). No >/< ordering.
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        var now = timeProvider.GetUtcNow();
        var workspace = Workspace.Create(context.OrganizationId, canonicalSlug, request.Name!.Trim(), now);

        var addResult = await deps.Workspaces.AddAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (addResult == WorkspaceAddResult.DuplicateSlug)
        {
            // Duplicate workspace slug within the organization -> 409 Conflict
            // (docs/08_API_CONTRACTS.md). The error carries no other tenant data.
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: "A workspace with this slug already exists in the organization.");
        }

        var response = WorkspaceResponse.From(workspace);
        return Results.Created($"/api/v1/workspaces/{workspace.Id}", response);
    }

    // GET /api/v1/workspaces/{workspaceId}?organizationSlug={slug}
    private static async Task<IResult> GetWorkspaceAsync(
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

        // A malformed workspace id can never address a stored workspace; treat it
        // as hidden (404), never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 so a foreign
            // or non-existent tenant is indistinguishable from a missing
            // workspace (docs/08: "404 = not found or intentionally hidden";
            // threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS
        // workspace. A non-member, or a workspace in another tenant, is hidden as
        // 404 (not 403) so resource existence is not leaked (threats T1/T5).
        var isMember = await deps.WorkspaceMembers
            .IsMemberAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!isMember)
        {
            return HiddenWorkspace();
        }

        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        return Results.Ok(WorkspaceResponse.From(workspace));
    }

    // PUT /api/v1/workspaces/{workspaceId}
    private static async Task<IResult> UpdateWorkspaceAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] UpdateWorkspaceRequest? request,
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

        if (!Workspace.IsValidName(request.Name?.Trim()))
        {
            return ValidationError("A valid workspace name is required.");
        }

        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (the workspace, if any, is
            // in a tenant the caller cannot see; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The workspace must exist within the tenant; otherwise hide as 404.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // "Manage workspace settings" is Owner/Admin only
        // (docs/06_AUTHORIZATION_MATRIX.md). The caller is a known member of the
        // tenant and the workspace exists, so an insufficient role is a 403
        // (authorized to see the workspace exists in their tenant, but not to
        // change it). Exact, non-linear role check.
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Rename only: the organization, slug and id are immutable, so the
        // workspace never moves tenant (threat T5).
        var now = timeProvider.GetUtcNow();
        workspace.Rename(request.Name!.Trim(), now);
        await deps.Workspaces.UpdateAsync(workspace, cancellationToken).ConfigureAwait(false);

        return Results.Ok(WorkspaceResponse.From(workspace));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent,
    /// the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out WorkspaceEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();

        if (resolver is null || workspaces is null || workspaceMembers is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new WorkspaceEndpointDependencies(resolver, workspaces, workspaceMembers);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields
    /// <see langword="false"/> (the caller returns 401). The mapping error reason
    /// is never echoed to the response (threat T7).
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
            detail: "Workspace operations require persistence, which is not configured.");

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

    // Tenant existence is hidden: a non-existent, foreign, or non-entitled
    // organization is reported as 404 for reads, never distinguishable from each
    // other and never echoing the reason (docs/08; threat T5).
    private static IResult HiddenOrganization() => NotFound();

    private static IResult HiddenWorkspace() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct WorkspaceEndpointDependencies(
        TenantContextResolver Resolver,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers);
}
