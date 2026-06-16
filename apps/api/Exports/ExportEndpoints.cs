using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Exports;

/// <summary>
/// HTTP endpoint of the Exports module's export READ/DOWNLOAD (CORE-EXP-001, the "Vertical Authoring and Read
/// API Completeness" epic: "Add the export read and download endpoint"). Until now the Exports module had ZERO
/// HTTP surface — the export job aggregate (<see cref="ExportJob"/>, CORE-AUD-002), the produced workspace
/// export manifest with its role-based projection (<see cref="ExportManifestProjection"/>, CORE-AUD-003), their
/// tenant-scoped persistence and EF migrations existed and were unit-tested, and the worker produced a manifest
/// for every queued export (CORE-JOB-002), but no route was mapped (csv/api_routes.csv defined none, recorded as
/// a deliberate Core-later deferral in docs/24_SPEC_CONSISTENCY.md, CORE-SPEC-003). An authorized host therefore
/// could not retrieve a completed export. This story adds that route ON TOP of the existing repositories +
/// projection, without a parallel export engine.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/exports/{exportId}</c> — module <b>Exports</b>, roles "Owner,Admin,Host", a
///   tenant/workspace-scoped read of a completed workspace export's artifact (its produced manifest),
///   role-projected. The route path carries only the <c>{exportId}</c> (the export JOB id), so the target tenant
///   is the required <c>?organizationSlug=</c> query parameter exactly like the asset signed-download and recap
///   read routes.</item>
/// </list>
///
/// THE ARTIFACT AND ITS DELIVERY (acceptance criterion "delivered by a short-lived signed URL or an authorized
/// stream, never a public/static URL"; threats T4/T8). In the Core model a completed workspace export's produced
/// artifact is its <see cref="ExportManifest"/> — the durable, product-neutral TABLE OF CONTENTS of what the
/// export covered (per-kind counts only, never any exported scene/content body; threats T7/T8). The Core stores
/// no separate export blob in object storage, so the artifact is delivered as an AUTHORIZED STREAM — the
/// role-projected manifest in this authenticated, authorized endpoint's response body — and NEVER through a
/// public or static URL: the manifest lives only in the tenant-scoped database and is reachable only through
/// this server-side permission check, exactly as the asset flow never hands out a public bucket URL (threat T4).
/// A retention-based expiry of the artifact (a true <c>ExpiresAt</c> with an object-storage purge) is a separate
/// later story (the data-retention sweeps, CORE-PRIV-003); until then the only states that gate the download are
/// the export's own lifecycle status (below).
///
/// AUTHORIZATION (object-level, server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md "Export workspace";
/// threats T1/T5/T8), authorize-BEFORE-produce exactly like the asset signed-download route (the URL/stream is
/// produced only AFTER every check passes), load-then-authorize and never leaking why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's
///   <see cref="System.Security.Claims.ClaimsPrincipal"/>; a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed/empty export id and a denied tenant
///   resolution (a service account, a foreign/unclaimed/unknown tenant) are hidden as 404 — a caller who cannot
///   see the tenant must not learn whether the export exists (threat T5).</item>
///   <item>The export job is loaded WITHIN the resolved tenant
///   (<see cref="IExportJobRepository.FindByIdInOrganizationAsync"/>, leading with the organization id), so a
///   job in another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown
///   export is hidden as 404 (threats T1/T5). The job's own workspace id is discovered from the loaded row AFTER
///   the tenant boundary is enforced.</item>
///   <item>A caller who is not a member of the export's workspace is ALSO hidden as 404 (a non-member must not
///   learn the export exists; the same object-level rule as the asset/recap routes), never 403.</item>
///   <item>A known workspace member who is not an authorized downloader is 403. The authorized set is the
///   "Export workspace" roles {Owner, Admin, Host} (<see cref="ExportAccessPolicy.CanDownloadExport"/>, an EXACT
///   non-linear set membership; CoHost, the audience roles Participant/Observer, and the deployment-optional
///   Auditor all fail closed). This is the "non-authoring role 403" acceptance criterion: a participant-scoped
///   (audience) caller is denied outright, so a participant never receives any host-only export content.</item>
///   <item>Only AFTER authorization is the export's availability checked, so an unauthorized caller never learns
///   its state: an export that is not <see cref="ExportJobStatus.Completed"/> (still pending/running, or failed)
///   has no retrievable artifact and is 409 — and, defensively, a completed job whose manifest is not present is
///   409 too. This is the "expired or incomplete export is rejected" acceptance criterion (mirrors the asset
///   signed-download 409 for a still-pending asset).</item>
///   <item>A downloadable, completed export is returned 200 as its artifact PROJECTED BY ROLE through the
///   existing <see cref="ExportManifestProjection"/> — defence in depth that keeps the export shape role-scoped
///   even though only full-view roles reach this point (threat T8 "export role-based projection").</item>
/// </list>
///
/// Persistence dependency: like the asset/recap endpoints, this uses the tenant context resolver, the
/// workspace-member repository and the export job + manifest repositories, registered only when a database
/// connection string is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed
/// with 503.
/// </summary>
internal static class ExportEndpoints
{
    /// <summary>Required query parameter naming the target organization (the tenant the export belongs to).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token
        // is challenged as 401 before any handler runs.
        var group = endpoints
            .MapGroup("/api/v1/exports")
            .RequireAuthorization();

        group.MapGet("/{exportId}", ReadExportAsync);

        return endpoints;
    }

    // GET /api/v1/exports/{exportId}?organizationSlug={slug}
    private static async Task<IResult> ReadExportAsync(
        HttpContext httpContext,
        string exportId,
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

        // The target organization is required and supplied by the request; the route path carries only the
        // export id, so it is a query parameter exactly like the asset signed-download and recap read routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty export id can never address a stored export; hidden as 404, never echoing why.
        if (!Guid.TryParse(exportId, out var exportGuid) || exportGuid == Guid.Empty)
        {
            return HiddenExport();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the export as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing export (docs/08; threat T5).
            return HiddenExport();
        }

        var context = resolution.Context;

        // Load the export job WITHIN the resolved tenant. The lookup leads with the organization id, so a job
        // in another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown
        // export is hidden as 404 (threats T1/T5). The job's own workspace id is then discovered from the
        // loaded row, AFTER the tenant boundary has been enforced — the same shape as the asset signed-download
        // route.
        var job = await deps.ExportJobs
            .FindByIdInOrganizationAsync(context.OrganizationId, exportGuid, cancellationToken)
            .ConfigureAwait(false);
        if (job is null)
        {
            return HiddenExport();
        }

        // Object-level authorization: the caller must be a member of the EXPORT'S own workspace. A caller who is
        // a member of the tenant but NOT of the export's workspace must not learn the export exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the asset/recap routes (threats T1/T5). The
        // member's actual workspace ROLE then drives the download gate below, scoped by organization id then the
        // job's own workspace id, so a role held in a DIFFERENT workspace never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, job.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenExport();
        }

        // The caller is a known member of the export's workspace, so an insufficient role is 403. The download
        // roles are the "Export workspace" set {Owner, Admin, Host} (ExportAccessPolicy.CanDownloadExport);
        // CoHost, the audience roles Participant/Observer and the deployment-optional Auditor all fail closed.
        // This is the "non-authoring role 403" criterion — a participant-scoped (audience) caller is denied
        // outright, so a participant never receives any host-only export content (threats T1/T5/T8).
        // MembershipRole is non-linear, so the gate is EXACT set membership, never an ordering comparison.
        if (!ExportAccessPolicy.CanDownloadExport(member.Role))
        {
            return Forbidden();
        }

        // Authorized. Only NOW is the export's availability checked, so an unauthorized caller never learns its
        // state. An export that has not settled Completed (still pending/running, or failed) has no retrievable
        // artifact, so it is rejected 409 — the "expired or incomplete export is rejected" criterion, mirroring
        // the asset signed-download 409 for a still-pending asset. The check is AFTER authorization, so only an
        // authorized downloader ever learns the export is not yet downloadable.
        if (job.Status != ExportJobStatus.Completed)
        {
            return ExportNotDownloadable();
        }

        // Load the produced artifact — the workspace export manifest — tenant- AND workspace-scoped (reusing the
        // existing repository, never a parallel lookup): the lookup leads with the organization id then the
        // workspace id, so another tenant's or workspace's manifest is never returned even when the job id
        // matches (threats T1/T5). A completed job always has exactly one manifest (the worker commits them
        // atomically under the unique export_manifests(export_job_id) index); if it is somehow absent the artifact
        // is not retrievable, so this is a fail-closed 409 (defence in depth), never a partial or empty body.
        var manifest = await deps.ExportManifests
            .FindByExportJobIdAsync(context.OrganizationId, job.WorkspaceId, job.Id, cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            return ExportNotDownloadable();
        }

        // PROJECT BY ROLE through the SAME projector the worker journey exercises (CORE-E2E-003): only the
        // authorized full-view roles reach this point, so they receive the full ExportManifestView WITH the
        // per-kind inventory; running the projector keeps the export shape role-scoped as defence in depth and
        // honours the "export role-based projection" control (threat T8). The artifact is produced — delivered as
        // this authorized stream — only after every authorization step above has passed.
        var response = ExportManifestProjection.Project(manifest, member.Role);
        return Results.Ok(response);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out ExportEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var exportJobs = services.GetService<IExportJobRepository>();
        var exportManifests = services.GetService<IExportManifestRepository>();

        if (resolver is null || workspaceMembers is null || exportJobs is null || exportManifests is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new ExportEndpointDependencies(resolver, workspaceMembers, exportJobs, exportManifests);
        return true;
    }

    /// <summary>
    /// Maps the authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/> to an
    /// <see cref="OidcPrincipal"/>, fail-closed: a failed mapping yields <see langword="false"/> (the caller
    /// returns 401). The mapping error reason is never echoed to the response (threat T7).
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
            detail: "Reading an export requires persistence, which is not configured.");

    private static IResult Unauthorized()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status401Unauthorized,
            code: ProblemCodes.AuthenticationRequired,
            title: "Unauthorized",
            detail: "Valid authentication is required.");

    private static IResult MissingOrganization()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    private static IResult Forbidden()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status403Forbidden,
            code: ProblemCodes.PermissionDenied,
            title: "Forbidden",
            detail: "You are not authorized to perform this action.");

    // Export existence is hidden: a malformed export id, an export in a foreign or non-entitled tenant, an
    // unknown export, and an export in a workspace the caller does not belong to are ALL reported as 404, never
    // distinguishable from each other and never echoing the reason (docs/08; threats T1/T5/T8).
    private static IResult HiddenExport()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    // An authorized downloader asked for an export that is not a retrievable completed artifact — it is still
    // pending/running, it failed, or (defensively) it completed without a manifest. Reported as 409 (an
    // out-of-state request) AFTER authorization, so only an authorized downloader ever learns the export is not
    // available (mirrors the asset signed-download 409 for a still-pending asset).
    private static IResult ExportNotDownloadable()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: "The export is not available for download.");

    private readonly record struct ExportEndpointDependencies(
        TenantContextResolver Resolver,
        IWorkspaceMemberRepository WorkspaceMembers,
        IExportJobRepository ExportJobs,
        IExportManifestRepository ExportManifests);
}
