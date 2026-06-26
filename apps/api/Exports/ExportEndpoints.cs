// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.SystemModule;
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
/// Routes owned by this module (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/exports/{exportId}</c> — module <b>Exports</b>, roles "Owner,Admin,Host", a
///   tenant/workspace-scoped read of a completed workspace export's artifact (its produced manifest),
///   role-projected (CORE-EXP-001). The route path carries only the <c>{exportId}</c> (the export JOB id), so the
///   target tenant is the required <c>?organizationSlug=</c> query parameter exactly like the asset
///   signed-download and recap read routes.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/exports</c> — module <b>Exports</b>, roles
///   "Owner,Admin,Host" (CORE-EXP-003, the "Async Export Request Lifecycle" epic). REQUESTS an async workspace
///   export — <see cref="ExportJob.Create"/> + <see cref="IExportJobRepository.AddAsync"/> mint a Pending
///   <see cref="ExportScope.Workspace"/> job and return <c>201</c> with the export id — so the worker export
///   PRODUCER (CORE-EXP-002) finally has a queue to drain and the read route above can be given a REAL id (it
///   could mint none before, the gap ARC-GAP-119). The request reuses the existing <c>export_jobs</c> table (no
///   schema change), emits NO session event (an export carries no session id) and adds NO event-catalog row. It
///   honors an OPTIONAL client <c>Idempotency-Key</c> (CORE-DX-004) through the shared
///   <see cref="IdempotentResourceCreator"/> under the per-tenant scope
///   <c>workspace-export-create:{organizationId}</c>: the FIRST request records the key against the minted
///   export id and returns <c>201</c>; a RETRY under the SAME key returns <c>200</c> with the ORIGINAL export id
///   and creates no second job; a malformed key is a <c>400</c> rejected AFTER authorization that creates
///   nothing. Object-level authorization is the SAME fail-closed rule as the read: a non-member of the route's
///   workspace is hidden as <c>404</c>; a known member who is not an authoring downloader (the "Export workspace"
///   set, <see cref="ExportAccessPolicy.CanRequestExport"/>) is <c>403</c>.</item>
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
///   its state: an export that is not <see cref="ExportJobStatus.Completed"/> has no retrievable artifact and is
///   409 — and, defensively, a completed job whose manifest is not present is 409 too. This is the "expired or
///   incomplete export is rejected" acceptance criterion (mirrors the asset signed-download 409 for a
///   still-pending asset). A job the worker DEAD-LETTERED (terminal <see cref="ExportJobStatus.Failed"/>,
///   CORE-RES-002) gets a DISTINCT 409 detail so a requester learns the export permanently failed rather than
///   mistaking it for one still in progress — surfaced only to an authorized downloader, so the state is never
///   disclosed to an unauthorized caller (threats T7/T8).</item>
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

        // The async export-REQUEST route (CORE-EXP-003) is WORKSPACE-scoped (its path roots at the workspaces
        // collection), so it lives under a second authenticated group — mirroring how the workspace-scoped
        // scene/entity create routes are mounted — rather than under the by-export-id group above.
        var workspaceScopedGroup = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScopedGroup.MapPost("/{workspaceId}/exports", RequestWorkspaceExportAsync);

        return endpoints;
    }

    // GET /api/v1/exports/{exportId}?organizationSlug={slug}
    private static async Task<IResult> ReadExportAsync(
        HttpContext httpContext,
        string exportId,
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

        // Branch on the export's explicit, immutable SCOPE (CORE-EXP-002). A user-data export discloses a data
        // subject's personal data and is authorized to the subject themselves or an Owner/Admin (the
        // access/portability authorization model of CORE-PRIV-004) — NOT the workspace-membership/role model a
        // workspace export uses — so it is handled on its own path. The workspace export path (below) is
        // unchanged.
        if (job.Scope == ExportScope.UserData)
        {
            return await ReadUserDataExportAsync(deps, context, job, timeProvider, cancellationToken)
                .ConfigureAwait(false);
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
        // state. A job that the worker DEAD-LETTERED (terminal Failed, CORE-RES-002) is reported distinctly so a
        // requester polling this route learns the export permanently FAILED — it surfaces as failed rather than
        // looking the same as a still-processing export — instead of waiting on a job that is stuck Pending
        // forever. Both responses are 409 (an out-of-state request) and disclosed only AFTER authorization, so
        // an unauthorized caller still learns nothing of the export's state; the failure detail names no content
        // or internal reason (threats T7/T8).
        if (job.Status == ExportJobStatus.Failed)
        {
            return ExportFailed();
        }

        // An export that has not settled Completed (still pending/running) has no retrievable artifact yet, so it
        // is rejected 409 — the "expired or incomplete export is rejected" criterion, mirroring the asset
        // signed-download 409 for a still-pending asset.
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
    /// Requests an async WORKSPACE export (CORE-EXP-003, the "Async Export Request Lifecycle" epic): mints a
    /// Pending <see cref="ExportScope.Workspace"/> <see cref="ExportJob"/> in the route's workspace so the worker
    /// export producer (CORE-EXP-002) has a queue to drain and the read route can be given a real export id. It is
    /// the WRITE half of the export lifecycle, sitting on the SAME tenant/workspace resolution, authorization and
    /// repositories as the read — no parallel export engine.
    ///
    /// <para>
    /// AUTHORIZATION (object-level, server-side, fail-closed; docs/06_AUTHORIZATION_MATRIX.md "Export workspace";
    /// threats T1/T5), load-then-authorize and never leaking why — the SAME shape as the read's workspace path:
    /// a failed principal mapping is 401; a missing body or blank <c>organizationSlug</c> is 400; a malformed
    /// workspace id and a denied tenant resolution are hidden as 404; a caller who is not a member of the route's
    /// workspace is ALSO hidden as 404 (a non-member must not learn the workspace exists), never 403; and a known
    /// member who is not an authoring requester (the "Export workspace" set {Owner, Admin, Host},
    /// <see cref="ExportAccessPolicy.CanRequestExport"/> — an EXACT non-linear set membership) is 403. The
    /// requester recorded on the job is the authenticated caller's own resolved profile, never a client-supplied
    /// id (threats T1/T5).
    /// </para>
    ///
    /// <para>
    /// IDEMPOTENCY (CORE-DX-004). After authorization an OPTIONAL client <c>Idempotency-Key</c> is honored through
    /// the shared <see cref="IdempotentResourceCreator"/> under the per-tenant scope
    /// <c>workspace-export-create:{organizationId}</c>: the create (the aggregate factory + the tenant-scoped
    /// <see cref="IExportJobRepository.AddAsync"/>) and the key record commit in ONE transaction, so the unique
    /// <c>idempotency_keys(scope, key)</c> index closes the concurrent-first-request race. The FIRST request
    /// returns 201 with the minted export id; a RETRY under the SAME key replays the ORIGINAL job (re-loaded
    /// tenant- AND workspace-scoped) and returns 200, creating no second job. A key reused against a DIFFERENT
    /// workspace re-loads to nothing and is a hidden 404 (threats T1/T5). A malformed key is a 400, rejected only
    /// AFTER authorization so an unauthorized caller never receives request-shape feedback (the reveal rule). An
    /// export carries no session id, so NO session event is emitted and NO event-catalog row is added.
    /// </para>
    /// </summary>
    // POST /api/v1/workspaces/{workspaceId}/exports
    private static async Task<IResult> RequestWorkspaceExportAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateExportRequest? request,
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

        // The target organization is required and supplied by the request body, mirroring the scene/entity
        // create routes (the route path carries only the workspace, not the organization).
        if (string.IsNullOrWhiteSpace(request.OrganizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed/empty workspace id can never address a stored workspace; hidden as 404, never echoing why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenExport();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing workspace (threat T5).
            return HiddenExport();
        }

        var context = resolution.Context;

        // Object-level authorization: the caller must be a member of THIS workspace. A caller who is a member of
        // the tenant but NOT of the workspace must not learn the workspace exists, so a missing membership is
        // hidden as 404 (not 403) — the SAME rule as the export read and the scene/entity create routes (threats
        // T1/T5). The member's workspace role then drives the role check below.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenExport();
        }

        // The caller is a known member of the workspace, so an insufficient role is 403. Requesting a workspace
        // export is the "Export workspace" permission, the SAME {Owner, Admin, Host} set as downloading it
        // (ExportAccessPolicy.CanRequestExport); CoHost, the audience roles Participant/Observer and the
        // deployment-optional Auditor all fail closed. MembershipRole is non-linear, so this is an EXACT set
        // membership check, never an ordering comparison.
        if (!ExportAccessPolicy.CanRequestExport(member.Role))
        {
            return Forbidden();
        }

        // Honor an OPTIONAL client Idempotency-Key (CORE-DX-004): a create/network retry under the same key
        // returns the ORIGINAL export job and creates exactly one. A malformed key is a 400, checked AFTER
        // authorization so an unauthorized caller never receives request-shape feedback (the reveal rule).
        if (IdempotencyKeyHeader.Read(httpContext, out var idempotencyKey) == IdempotencyKeyHeaderResult.Invalid)
        {
            return ValidationError($"The '{IdempotencyKeyHeader.HeaderName}' header is malformed.");
        }

        // Authorized: mint the Pending workspace export job through the existing aggregate factory and the
        // tenant-scoped repository. When a key is supplied, the job insert AND the idempotency-key record commit
        // together in ONE transaction the IdempotentResourceCreator owns (scope per tenant and operation). The job
        // always has a server-minted id, so its result-id selector never returns null and the key is always
        // recorded on a fresh create (an export request never becomes a no-op business conflict).
        var now = timeProvider.GetUtcNow();
        var creation = await deps.Idempotency
            .CreateAsync(
                idempotencyKey,
                $"workspace-export-create:{context.OrganizationId}",
                now,
                create: async createCancellationToken =>
                {
                    var job = ExportJob.Create(
                        context.OrganizationId,
                        workspaceGuid,
                        context.UserProfileId,
                        ExportScope.Workspace,
                        now);
                    await deps.ExportJobs.AddAsync(job, createCancellationToken).ConfigureAwait(false);
                    return job;
                },
                resultIdSelector: job => job.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (creation.Replayed)
        {
            // A prior request with this key already minted the export job; return the ORIGINAL (200), creating no
            // second job. The job is re-loaded through the tenant- AND workspace-scoped FindByIdAsync, so a key
            // reused against a different workspace resolves to nothing and is a hidden 404 (threats T1/T5). The
            // requester role here is always a full-view authoring role, so the full ExportJobView is returned.
            var original = await deps.ExportJobs
                .FindByIdAsync(context.OrganizationId, workspaceGuid, creation.ReplayResultId, cancellationToken)
                .ConfigureAwait(false);
            return original is null
                ? HiddenExport()
                : Results.Ok(ExportJobView.From(original));
        }

        // A fresh request: 201 Created with the minted export job. The Location points at the by-export-id read
        // route, so a client can immediately poll the export it just requested.
        var created = creation.Result;
        return Results.Created($"/api/v1/exports/{created.Id}", ExportJobView.From(created));
    }

    /// <summary>
    /// Serves a USER-DATA export (CORE-EXP-002, GDPR Art.15 access / Art.20 portability): the disclosure half of
    /// the producer that this story adds. The export job has already been loaded WITHIN the resolved tenant
    /// (so a foreign-tenant/unknown export is already hidden 404); this method authorizes the disclosure, gates
    /// it on the export's availability and, only then, assembles and returns the data subject's personal data.
    ///
    /// <para>
    /// THE SUBJECT. A user-data export discloses the personal data of its REQUESTER — the authenticated subject
    /// who requested their own data (the job's immutable <see cref="ExportJob.RequestedByUserProfileId"/>). So
    /// the export contains ONLY the requesting subject's data (the acceptance criterion); it never widens into
    /// another subject's data or into workspace-wide host content (threat T8).
    /// </para>
    ///
    /// <para>
    /// AUTHORIZATION (the access/portability model of CORE-PRIV-004; docs/06_AUTHORIZATION_MATRIX.md "Export data
    /// subject personal data"; threats T1/T5). The PII is delivered ONLY to the entitled recipient: the data
    /// SUBJECT themselves (self-service — the caller's resolved profile matches the export's subject) OR an
    /// Owner/Admin of the resolved tenant (the data controller acting on the subject's behalf). The caller is
    /// already a known member of the tenant (the resolution proved it), so any OTHER tenant member — a
    /// Host/CoHost/Participant/Observer/Auditor who is neither the subject nor an Owner/Admin — is denied 403,
    /// fail-closed. Exact, non-linear role checks (no &gt;/&lt;).
    /// </para>
    ///
    /// <para>
    /// AVAILABILITY then DISCLOSURE. Only AFTER authorization is the export's state checked, so an unauthorized
    /// caller never learns it: a dead-lettered (terminal Failed) export is a distinct 409, a not-yet-produced
    /// (pending/running) export is a 409, and an export whose subject was since erased (the requester column is
    /// SET NULL) is a 409 — no personal data to disclose. A produced, completed export is assembled TENANT-SCOPED
    /// and audited through the existing <see cref="PersonalDataExportService"/> (an append-only
    /// <c>PersonalDataExported</c> fact by id — the actor and the exported subject — never the disclosed PII;
    /// threats T1/T5/T7) and returned as the authorized stream (the response body), never a public/static URL
    /// (threat T4).
    /// </para>
    /// </summary>
    private static async Task<IResult> ReadUserDataExportAsync(
        ExportEndpointDependencies deps,
        TenantContext context,
        ExportJob job,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // The export discloses the requester's OWN personal data. Authorize the disclosure to the data subject
        // themselves OR an Owner/Admin of the resolved tenant; any other tenant member is 403, fail-closed. The
        // self check matches the caller's resolved user profile against the export's requester (a null requester —
        // an anonymized/erased subject — can never match a real caller).
        var callerIsSubject = job.RequestedByUserProfileId is { } subjectId
            && subjectId == context.UserProfileId;
        var callerIsTenantAdmin = context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin);
        if (!(callerIsSubject || callerIsTenantAdmin))
        {
            return Forbidden();
        }

        // Authorized. Only NOW is the export's availability checked, so an unauthorized caller never learns its
        // state. A dead-lettered (terminal Failed) export surfaces distinctly so a requester polling the route
        // learns it permanently failed; an export that has not settled Completed (still pending/running) has no
        // retrievable personal data yet — both 409, disclosed only after authorization (threats T7/T8).
        if (job.Status == ExportJobStatus.Failed)
        {
            return ExportFailed();
        }

        if (job.Status != ExportJobStatus.Completed)
        {
            return ExportNotDownloadable();
        }

        // The export's subject must still exist. The requester column is SET NULL when the subject is erased
        // (GDPR Art.17), so a null requester means the personal data is gone — there is nothing to disclose, a
        // fail-closed 409 (defence in depth; an Owner/Admin who reaches here learns only that it is unavailable).
        if (job.RequestedByUserProfileId is not { } subject)
        {
            return ExportNotDownloadable();
        }

        // Assemble the subject's tenant-scoped personal data and audit the disclosure through the SAME service the
        // synchronous access/portability route uses (CORE-PRIV-004) — no parallel assembly path. The actor is the
        // caller who obtained the export (the subject or the admin); the audited subject is the export's subject.
        var now = timeProvider.GetUtcNow();
        var export = await deps.PersonalDataExport
            .ExportAsync(context.OrganizationId, context.UserProfileId, subject, now, cancellationToken)
            .ConfigureAwait(false);

        // Defensive: the requester is a foreign key into a real user profile, so the subject should always exist;
        // if it somehow does not, fail closed rather than disclose anything.
        return export is null
            ? ExportNotDownloadable()
            : Results.Ok(PersonalDataExportResponse.From(export));
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
        var personalDataExport = services.GetService<PersonalDataExportService>();
        var idempotency = services.GetService<IdempotentResourceCreator>();

        if (resolver is null
            || workspaceMembers is null
            || exportJobs is null
            || exportManifests is null
            || personalDataExport is null
            || idempotency is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new ExportEndpointDependencies(
            resolver, workspaceMembers, exportJobs, exportManifests, personalDataExport, idempotency);
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
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: detail);

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
    // pending/running, or (defensively) it completed without a manifest. Reported as 409 (an out-of-state
    // request) AFTER authorization, so only an authorized downloader ever learns the export is not available
    // (mirrors the asset signed-download 409 for a still-pending asset).
    private static IResult ExportNotDownloadable()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: "The export is not available for download.");

    // An authorized downloader asked for an export the worker DEAD-LETTERED (terminal Failed, CORE-RES-002).
    // Reported as 409 (an out-of-state request) AFTER authorization with a distinct detail so the requester
    // learns the export permanently FAILED rather than mistaking it for one still in progress. The detail names
    // no content or internal failure reason (threats T7/T8).
    private static IResult ExportFailed()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: "The export failed and is not available for download.");

    private readonly record struct ExportEndpointDependencies(
        TenantContextResolver Resolver,
        IWorkspaceMemberRepository WorkspaceMembers,
        IExportJobRepository ExportJobs,
        IExportManifestRepository ExportManifests,
        PersonalDataExportService PersonalDataExport,
        IdempotentResourceCreator Idempotency);
}
