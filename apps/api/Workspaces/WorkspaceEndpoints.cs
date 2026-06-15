using System.Security.Claims;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
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
///   slice), "Manage workspace settings" (Owner or Admin). Rejected with 409 when
///   the workspace is archived (read-only; CORE-LIFE-009).</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/archive</c> — archive the
///   workspace (CORE-LIFE-009), the "Delete workspace" matrix row, OWNER-ONLY. A
///   soft, audited, terminal status transition (Active -> Archived) that makes the
///   workspace read-only and excludes it from the active list while preserving its
///   children and audit trail; an already-archived workspace is a 409. Appends a
///   <see cref="AuditAction.WorkspaceArchived"/> audit record. Fail-closed and
///   hidden as 404 for a cross-tenant/unknown workspace.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/members</c> — invite a
///   member (CORE-WS-004), "Manage members" (Owner or Admin). Creates a scoped,
///   single-use invite token and returns the plaintext token exactly once; only
///   its hash is stored (threats T6/T7). The matching acceptance/redeem route is
///   CORE-WS-006 below; email delivery and UI remain out of scope.</item>
///   <item><c>GET /api/v1/workspaces/{workspaceId}/invitations</c> — list a
///   workspace's PENDING invitations (CORE-WS-008), "Manage members" (Owner or
///   Admin), so the manage-members surface can see its outstanding invites. Returns
///   a PII-safe projection (id, invited email, role, status, expiry) and NEVER the
///   token hash (threats T6/T7); the invited email is the only personal datum.
///   Tenant/workspace-scoped and fail-closed: a foreign or unknown workspace is an
///   indistinguishable hidden 404, a known tenant member without Owner/Admin is a
///   403.</item>
///   <item><c>POST /api/v1/workspaces/{workspaceId}/invitations/accept</c> —
///   redeem a workspace invitation (CORE-WS-006). The scoped token is a BEARER
///   grant (threat T6): the AUTHENTICATED CALLER who presents a valid token in the
///   request BODY (never the URL, threat T7) becomes the workspace member with the
///   invitation's role; the invited email is data, never an identity check. The
///   redeem is single-use, expiry- and revocation-aware and tenant/workspace-scoped,
///   and it is ATOMIC (CORE-CONC-002): the invitation is marked Accepted, the
///   <see cref="WorkspaceMember"/> is created and the join is audited
///   (<see cref="AuditAction.MemberJoined"/>) in one transaction. An invalid,
///   expired, revoked, already-redeemed or foreign token grants nothing and is an
///   indistinguishable hidden 404 (fail-closed). Open to any authenticated org
///   member (the token, not a role, is the grant); a caller already a member of the
///   workspace is a 409 that does not consume the token.</item>
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}</c> —
///   revoke a pending workspace invitation (CORE-WS-007), "Manage members" (Owner
///   or Admin), mirroring the member-invite authorization. Transitions the
///   invitation <c>Pending -> Revoked</c> so its scoped token can never be redeemed
///   (the threat T6 revocation control), and appends a
///   <see cref="AuditAction.MemberInvitationRevoked"/> audit record. Only a pending
///   invitation may be revoked: an already-accepted or already-revoked invitation is
///   a 409 that changes nothing (placed AFTER the role check so a non-Owner/Admin
///   still gets a 403 and never learns the invitation state). Fail-closed and hidden
///   as 404 for a cross-tenant/unknown workspace or invitation.</item>
///   <item><c>DELETE /api/v1/workspaces/{workspaceId}/members/{memberId}</c> —
///   remove a workspace member (CORE-LIFE-001), "Manage members" (Owner or
///   Admin). Hard-deletes the membership, revoking the subject's access on their
///   next request; the sole workspace Owner cannot be removed (the last-Owner
///   invariant, 409); the removal is appended to the append-only audit log
///   (<see cref="AuditAction.MemberRemoved"/>). Fail-closed and hidden as 404 for
///   a cross-tenant/unknown workspace or member.</item>
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
///   <item>Per-action authorization is inline here. CORE-WS-005 adds a systematic
///   authorization test matrix over these checks
///   (tests/LiveCore.Api.IntegrationTests/WorkspaceAuthorizationPolicyTests.cs);
///   a reusable policy/handler framework is intentionally not built.</item>
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
        group.MapPost("/{workspaceId}/archive", ArchiveWorkspaceAsync);
        group.MapPost("/{workspaceId}/members", InviteWorkspaceMemberAsync);
        group.MapGet("/{workspaceId}/invitations", ListPendingWorkspaceInvitationsAsync);
        group.MapPost("/{workspaceId}/invitations/accept", AcceptWorkspaceInvitationAsync);
        group.MapDelete("/{workspaceId}/invitations/{invitationId}", RevokeWorkspaceInvitationAsync);
        group.MapDelete("/{workspaceId}/members/{memberId}", RemoveWorkspaceMemberAsync);

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

        // Quota enforcement (CORE-ENTL-004; atomic per CORE-CONC-004): a workspace creation consumes one unit of the
        // creating user's workspace.active.max quota. The atomic check-and-consume runs AFTER role authorization (so
        // quota state is never consulted for an unauthorized caller) and BEFORE the create; it is computed entirely
        // server-side and is fail-closed, so a free user cannot exceed their limit by tampering with the client
        // ("Free limits cannot be bypassed by clients"; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The
        // consume is a SINGLE limit-guarded increment, so N concurrent creates at a limit of one yield exactly one
        // success and N-1 quota-exceeded (no time-of-check-to-time-of-use race). The slot is RESERVED first and
        // released below if the create then fails, so a rejected create never burns the user's allowance. When no
        // quota governs the deployment the command proceeds unchanged.
        var quotaDecision = await deps.QuotaEnforcement
            .TryConsumeAsync(
                EntitlementSubjectType.User,
                context.UserProfileId,
                QuotaEntitlementKeys.WorkspaceActiveMax,
                amount: 1,
                cancellationToken)
            .ConfigureAwait(false);
        if (!quotaDecision.IsAllowed)
        {
            // Record the denial as a real audit fact (CORE-SPEC-002: AuditAction.QuotaExceeded, the catalog's
            // QuotaExceeded event). It is a tenant-scoped fact: the caller (the audited actor) is denied for their
            // own User quota subject; the workspace does not exist yet, so there is no workspace scope.
            await deps.AuditLog
                .AppendAsync(
                    AuditLogEntry.ForQuotaExceeded(
                        context.OrganizationId,
                        workspaceId: null,
                        context.UserProfileId,
                        nameof(EntitlementSubjectType.User),
                        context.UserProfileId,
                        timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaExceeded(quotaDecision);
        }

        var now = timeProvider.GetUtcNow();
        var workspace = Workspace.Create(context.OrganizationId, canonicalSlug, request.Name!.Trim(), now);

        var addResult = await deps.Workspaces.AddAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (addResult == WorkspaceAddResult.DuplicateSlug)
        {
            // Duplicate workspace slug within the organization -> 409 Conflict
            // (docs/08_API_CONTRACTS.md). The error carries no other tenant data.
            // Compensate the reserved quota slot so a rejected create never burns
            // the user's allowance (a no-op when the deployment governs no quota).
            await deps.QuotaEnforcement
                .ReleaseAsync(
                    EntitlementSubjectType.User,
                    context.UserProfileId,
                    QuotaEntitlementKeys.WorkspaceActiveMax,
                    amount: 1,
                    cancellationToken)
                .ConfigureAwait(false);
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

        // Read-only when archived (CORE-LIFE-009): an archived workspace rejects
        // authoring mutations, so a rename is a 409 Conflict that changes nothing.
        // The caller is authorized; the lifecycle state, not the caller, is the
        // reason, so this is a 409 rather than a 403 (it is placed AFTER the role
        // check so a non-Owner/Admin still gets a 403 and never learns the
        // archived state). The detail names only the generic state (threat T7).
        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Rename only: the organization, slug and id are immutable, so the
        // workspace never moves tenant (threat T5).
        var now = timeProvider.GetUtcNow();
        workspace.Rename(request.Name!.Trim(), now);
        await deps.Workspaces.UpdateAsync(workspace, cancellationToken).ConfigureAwait(false);

        return Results.Ok(WorkspaceResponse.From(workspace));
    }

    // POST /api/v1/workspaces/{workspaceId}/archive?organizationSlug={slug}
    //
    // Archives a workspace (CORE-LIFE-009, the "Resource Lifecycle and Deletion" epic): a SOFT, audited,
    // OWNER-ONLY status transition (Active -> Archived) that makes the workspace read-only and excludes it
    // from the active list, while preserving its children and audit trail. The flow mirrors the workspace
    // by-id write routes (fail-closed, load-then-authorize, hidden-404): the tenant comes from the required
    // ?organizationSlug=, the workspace is loaded within that tenant, the caller is authorized as the
    // organization Owner, and only then is the status transition applied and the archive audited.
    private static async Task<IResult> ArchiveWorkspaceAsync(
        HttpContext httpContext,
        string workspaceId,
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
        // organization, so it is a query parameter exactly like the member-removal and other workspace
        // by-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404), never
        // echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (a foreign or non-existent tenant is indistinguishable
            // from a missing workspace; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The workspace must exist within the resolved tenant; otherwise hide as 404 (a cross-tenant or
        // unknown workspace is never revealed; threats T1/T5). The lookup is tenant-scoped by organization id.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // Archiving is the "Delete workspace" matrix row, which is OWNER-ONLY for Core's fail-closed default
        // (docs/06_AUTHORIZATION_MATRIX.md grants Owner "yes" and Admin only an "optional"; the story scopes
        // this Owner-only). The caller is a known member of the tenant and the workspace exists, so an
        // insufficient role is a 403. Exact, non-linear role check: ONLY Owner, never an ordering comparison
        // and never Admin.
        if (!context.HasRole(MembershipRole.Owner))
        {
            return Forbidden();
        }

        // The archive is terminal: an already-archived workspace cannot be archived again. The caller is
        // authorized; the lifecycle state, not the caller, is the reason, so this is a 409 Conflict that
        // changes nothing and writes no duplicate audit fact (CanArchive is the Active-only guard). Placed
        // AFTER the role check so a non-Owner gets a 403 and never learns the archived state (threat T7).
        if (!workspace.CanArchive)
        {
            return AlreadyArchivedConflict();
        }

        // Capture the previous status name BEFORE the transition for the audit record (the in-memory object
        // survives, but capturing first keeps the audited "before" state independent of the mutation below).
        var previousStatus = workspace.Status.ToString();

        var now = timeProvider.GetUtcNow();
        workspace.Archive(now);
        await deps.Workspaces.UpdateAsync(workspace, cancellationToken).ConfigureAwait(false);

        // QUOTA RELEASE (CORE-MON-007): archiving a workspace frees the creating user's active-workspace slot, so
        // release the unit consumed at create (CreateWorkspaceAsync consumes one workspace.active.max for the User
        // subject). Without this the active-list query excludes the archived workspace (WorkspaceRepository.cs:104)
        // while quota_usage keeps the consumption, so a free user (limit 1) is locked out forever after a single
        // create -> archive — the symmetric counterpart to how session end releases session.active.max
        // (SessionEndpoints.cs). The release is keyed on the SAME (User, key) pair the create consumes, so the
        // counter tracks the user's CURRENT active workspaces rather than a lifetime total. It is idempotent: the
        // decrement is clamped at zero and is a no-op when nothing is recorded, and it runs only AFTER the
        // transition is persisted (a concurrent second archive loses the optimistic-concurrency write above and is
        // rejected as 409 before reaching here, so it can never double-release), and a no-op when no quota governs
        // the deployment. Mirrors the create-side consume, which reserves before creating and releases on failure.
        await deps.QuotaEnforcement
            .ReleaseAsync(
                EntitlementSubjectType.User,
                context.UserProfileId,
                QuotaEntitlementKeys.WorkspaceActiveMax,
                amount: 1,
                cancellationToken)
            .ConfigureAwait(false);

        // AUDIT: an archive is a security-relevant lifecycle change, so append an append-only audit record
        // capturing the actor (the owner who archived it), the archived workspace and the Active -> Archived
        // status transition (threats T1/T5). Unlike a deletion, an archive records the before/after status
        // because the workspace survives the transition.
        var entry = AuditLogEntry.ForWorkspaceArchive(
            context.OrganizationId,
            workspace.Id,
            context.UserProfileId,
            nameof(Workspace),
            previousStatus,
            workspace.Status.ToString(),
            now);
        await deps.AuditLog.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // The workspace survives the archive (it is now read-only), so the updated DTO is returned with its
        // new lifecycle status, mirroring the session start/end status-transition commands.
        return Results.Ok(WorkspaceResponse.From(workspace));
    }

    // POST /api/v1/workspaces/{workspaceId}/members
    private static async Task<IResult> InviteWorkspaceMemberAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] InviteWorkspaceMemberRequest? request,
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

        // Validate the invite inputs before touching the tenant, so a broken
        // body is a 400 regardless of authorization. The email is data, not a
        // credential (docs/adr/0005); the role must be a defined generic role,
        // never an undefined value a cast could smuggle in (threat T6 role
        // limitation; MembershipRole is non-linear, so this is a parse + defined
        // check, never an ordering comparison).
        if (!WorkspaceInvitation.IsValidInvitedEmail(request.Email?.Trim()))
        {
            return ValidationError("A valid invited email is required.");
        }

        if (!TryParseRole(request.Role, out var role))
        {
            return ValidationError("A valid membership role is required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it
        // as hidden (404), never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the workspace as 404 so a foreign
            // or non-existent tenant is indistinguishable from a missing
            // workspace (threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The target workspace must exist within the resolved tenant; otherwise
        // hide as 404 (a cross-tenant or unknown workspace is never revealed;
        // threats T1/T5). The lookup is tenant-scoped by organization id.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md;
        // csv/api_routes.csv roles "Owner,Admin"). The caller is a known member
        // of the tenant and the workspace exists, so an insufficient role is a
        // 403. Exact, non-linear role check (no >/< ).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Read-only when archived (CORE-LIFE-009): an archived workspace rejects authoring mutations, so
        // inviting a new member is a 409 Conflict that creates nothing. Placed AFTER the role check so a
        // non-Owner/Admin still gets a 403 and never learns the archived state (threat T7).
        if (workspace.IsArchived)
        {
            return ArchivedReadOnly();
        }

        // Create the invitation. The aggregate generates the scoped token with a
        // cryptographically secure RNG, stores ONLY its SHA-256 hash, and hands
        // back the plaintext exactly once through the out parameter for the
        // response below; the plaintext is never persisted and never logged
        // (threats T6/T7).
        var now = timeProvider.GetUtcNow();
        var invitation = WorkspaceInvitation.Create(
            context.OrganizationId,
            workspace.Id,
            request.Email!.Trim(),
            role,
            now,
            out var plaintextToken);

        var addResult = await deps.WorkspaceInvitations
            .AddAsync(invitation, cancellationToken)
            .ConfigureAwait(false);
        if (addResult == WorkspaceInvitationAddResult.DuplicateToken)
        {
            // A token-hash collision is astronomically unlikely for a 256-bit
            // random token; if it ever happens, fail closed without leaking the
            // token rather than returning a non-unique invite.
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: "The invitation could not be created; retry.");
        }

        // The one-time token is returned to the caller here and only here.
        var response = WorkspaceInvitationResponse.From(invitation, plaintextToken);
        return Results.Created($"/api/v1/workspaces/{workspace.Id}/members/{invitation.Id}", response);
    }

    // GET /api/v1/workspaces/{workspaceId}/invitations?organizationSlug={slug}
    //
    // Lists a workspace's PENDING invitations (CORE-WS-008), so the manage-members surface can see its
    // outstanding invites (the read half of the invite flow). It returns a PII-safe projection — id, invited
    // email, role, status, expiry — and NEVER the token hash (threats T6/T7); the invited email is the only
    // personal datum. The flow mirrors the other workspace by-id reads (fail-closed, resolve-tenant,
    // load-then-authorize, hidden-404): the tenant comes from the required ?organizationSlug=, the workspace is
    // loaded within that tenant, and the caller is authorized as the organization Owner/Admin (the "Manage
    // members" matrix row, exactly like the member-invite/revoke siblings). A cross-tenant/unknown workspace is
    // an indistinguishable hidden 404; a known tenant member who is not Owner/Admin is a 403. The read takes no
    // clock: "pending" is the lifecycle status only, so an accepted or revoked invitation is never listed.
    private static async Task<IResult> ListPendingWorkspaceInvitationsAsync(
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

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the other workspace by-id routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404), never
        // echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (a foreign or non-existent tenant is indistinguishable
            // from a missing workspace; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The target workspace must exist within the resolved tenant; otherwise hide as 404 (a cross-tenant or
        // unknown workspace is never revealed; threats T1/T5). The lookup is tenant-scoped by organization id.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md; csv/api_routes.csv roles
        // "Owner,Admin"), authorized by the caller's ORGANIZATION role exactly like the invite/revoke siblings on
        // this same path. The caller is a known member of the tenant and the workspace exists, so an insufficient
        // role is a 403. Exact, non-linear role check (no >/<). Seeing the outstanding invites is itself a
        // manage-members capability, so a non-Owner/Admin is denied before any invitation is read.
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Read the pending invitations WITHIN this tenant and workspace; an invitation in another workspace or
        // tenant is never returned (threats T1/T5). The aggregates carry the token hash, but the response is the
        // PII-safe projection, which never emits it (threats T6/T7).
        var invitations = await deps.WorkspaceInvitations
            .ListPendingByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);

        var response = invitations.Select(PendingWorkspaceInvitationResponse.From).ToArray();
        return Results.Ok(response);
    }

    // POST /api/v1/workspaces/{workspaceId}/invitations/accept
    //
    // Redeems a workspace invitation (CORE-WS-006), the acceptance half of the invite flow (CORE-WS-004 shipped
    // only the invite). The scoped token is a BEARER grant (the decided model, threat T6): whoever presents a
    // valid token becomes the member, so the AUTHENTICATED CALLER's OIDC subject — never the invited email,
    // which is data only — is the one granted membership. The token travels in the request BODY, never the URL
    // path or query (a token in a URL leaks to logs/proxies/history; threat T7). The redeem is single-use,
    // expiry- and revocation-aware and tenant/workspace-scoped, and the invitation-accept + member-create +
    // audit are ATOMIC in one CORE-CONC-002 transaction. Fail-closed: an unknown, expired, revoked,
    // already-redeemed or foreign token is an indistinguishable hidden 404.
    private static async Task<IResult> AcceptWorkspaceInvitationAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] AcceptWorkspaceInvitationRequest? request,
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

        // The token is the bearer grant and MUST be present: a missing/blank token is a malformed request
        // (400), distinct from a well-formed-but-invalid token, which is a hidden 404 below. The token is read
        // from the body only and is never echoed back or logged (threats T6/T7).
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return ValidationError("An invitation token is required.");
        }

        // A malformed workspace id can never address a stored workspace; treat it as hidden (404), never
        // echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, request.OrganizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is indistinguishable
            // from a missing invitation (threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // Reduce the presented plaintext to its stored hash for the scoped lookup. Hashing is pure, so it runs
        // before the transaction; the plaintext is never persisted, compared in SQL or logged (threats T6/T7).
        var tokenHash = WorkspaceInvitationToken.Hash(request.Token);
        var now = timeProvider.GetUtcNow();

        // ONE unit of work (CORE-CONC-002): the single-use status transition (Pending -> Accepted), the
        // membership create and the append-only audit record commit together in a single database transaction,
        // so the WorkspaceMember is created and the invitation marked Accepted ATOMICALLY (a part-way failure
        // rolls BOTH back — never a consumed token without a membership, nor a membership without a consumed
        // token). The invitation is loaded INSIDE the unit of work so a retry re-reads fresh state
        // (CORE-CONC-005) and the redeem decision is made against the row the writes commit on. On PostgreSQL
        // the invitation carries an xmin concurrency token, so two concurrent redemptions of one token make the
        // second write conflict (409) rather than granting a second membership — the single-use guarantee holds
        // even under a race (threat T6).
        var outcome = await deps.UnitOfWork
            .ExecuteAsync(
                async transactionCancellationToken =>
                {
                    // Resolve the invitation by its token hash WITHIN the resolved tenant and the route's
                    // workspace, so a token minted for another workspace or tenant resolves to nothing here: a
                    // foreign token is an indistinguishable rejection (threats T5/T6).
                    var invitation = await deps.WorkspaceInvitations
                        .FindByTokenHashAsync(context.OrganizationId, workspaceGuid, tokenHash, transactionCancellationToken)
                        .ConfigureAwait(false);

                    // Fail closed: an unknown, expired, revoked or already-redeemed token grants nothing and is
                    // reported identically (a hidden 404), so a probe cannot tell them apart (threat T6). The
                    // status+expiry gate is IsRedeemable; the exact hash match is implicit in the scoped lookup.
                    if (invitation is null || !invitation.IsRedeemable(now))
                    {
                        return AcceptInvitationOutcome.Rejected();
                    }

                    // BEARER grant: the AUTHENTICATED CALLER (context.UserProfileId), never the invited email,
                    // becomes the member. A caller who already holds a membership in this workspace does not
                    // consume the token — nothing is mutated, so the (empty) transaction commits cleanly and the
                    // command is a 409 whose reason is the existing standing, not the token.
                    var alreadyMember = await deps.WorkspaceMembers
                        .IsMemberAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, transactionCancellationToken)
                        .ConfigureAwait(false);
                    if (alreadyMember)
                    {
                        return AcceptInvitationOutcome.AlreadyMember();
                    }

                    // Create the membership for the caller's subject FIRST, before consuming the token: the
                    // invitation grants exactly its own role and no other (threat T6 role limitation). Adding
                    // before redeeming means a lost create race (a concurrent first-time create that won) returns
                    // DuplicateMembership here and is handled as a 409 WITHOUT the token having been consumed —
                    // no rollback gymnastics, the invitation is simply left Pending.
                    var member = WorkspaceMember.Create(
                        context.OrganizationId, workspaceGuid, context.UserProfileId, invitation.Role, now);
                    var addResult = await deps.WorkspaceMembers
                        .AddAsync(member, transactionCancellationToken)
                        .ConfigureAwait(false);
                    if (addResult == WorkspaceMemberAddResult.DuplicateMembership)
                    {
                        return AcceptInvitationOutcome.AlreadyMember();
                    }

                    // Mark the single-use token redeemed (Pending -> Accepted). IsRedeemable was just checked, so
                    // this never throws; persisting it through the shared context enrols it in this transaction.
                    invitation.Redeem(now);
                    await deps.WorkspaceInvitations
                        .UpdateAsync(invitation, transactionCancellationToken)
                        .ConfigureAwait(false);

                    // AUDIT: a redemption grants access, so append an append-only audit record capturing the
                    // actor (the caller who redeemed), the created membership and the granted role (threat T6
                    // invite control). The invite token is never recorded — only the fact of the grant (T6/T7).
                    await deps.AuditLog
                        .AppendAsync(
                            AuditLogEntry.ForMemberJoined(
                                context.OrganizationId,
                                workspaceGuid,
                                context.UserProfileId,
                                nameof(WorkspaceMember),
                                member.Id,
                                invitation.Role.ToString(),
                                now),
                            transactionCancellationToken)
                        .ConfigureAwait(false);

                    return AcceptInvitationOutcome.Redeemed(member);
                },
                cancellationToken)
            .ConfigureAwait(false);

        return outcome.Kind switch
        {
            // The membership IS the access grant, so a successful redemption created a new resource: 201 with a
            // Location to the new member and the generic membership DTO (no token, no invited email; T6/T7).
            AcceptInvitationOutcomeKind.Redeemed => Results.Created(
                $"/api/v1/workspaces/{workspaceGuid}/members/{outcome.Member!.Id}",
                WorkspaceMemberResponse.From(outcome.Member!)),

            // The caller already had standing in the workspace; the token was not consumed.
            AcceptInvitationOutcomeKind.AlreadyMember => AlreadyMemberConflict(),

            // Fail-closed: an unknown, expired, revoked, already-redeemed or foreign token is an
            // indistinguishable hidden 404 (threat T6).
            _ => HiddenWorkspace(),
        };
    }

    // DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}?organizationSlug={slug}
    //
    // Revokes a pending workspace invitation (CORE-WS-007), the take-back half of the invite flow (CORE-WS-004
    // shipped only the invite; CORE-WS-006 the redeem). It transitions the invitation Pending -> Revoked so its
    // scoped token can never be redeemed afterwards (the threat T6 revocation control), and audits the change.
    // The flow mirrors the member-removal sibling on this same module (fail-closed, load-then-authorize,
    // hidden-404): the tenant comes from the required ?organizationSlug=, the workspace is loaded within that
    // tenant, the caller is authorized as the organization Owner/Admin (the "Manage members" matrix row, exactly
    // like the member-invite route), the invitation is loaded within that tenant AND workspace, and only then is
    // the status transition applied and the revoke audited. A cross-tenant/unknown workspace or invitation is an
    // indistinguishable hidden 404; an already-accepted or already-revoked invitation is a 409 that changes
    // nothing.
    private static async Task<IResult> RevokeWorkspaceInvitationAsync(
        HttpContext httpContext,
        string workspaceId,
        string invitationId,
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
        // organization, so it is a query parameter exactly like the member-removal and other workspace by-id
        // routes.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or invitation id can never address a stored row; treat each as hidden (404),
        // never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        if (!Guid.TryParse(invitationId, out var invitationGuid) || invitationGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (a foreign or non-existent tenant is indistinguishable
            // from a missing workspace; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The target workspace must exist within the resolved tenant; otherwise hide as 404 (a cross-tenant or
        // unknown workspace is never revealed; threats T1/T5). The lookup is tenant-scoped by organization id.
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md; csv/api_routes.csv roles
        // "Owner,Admin"), authorized by the caller's ORGANIZATION role exactly like the invite sibling on this
        // same path (POST .../members). The caller is a known member of the tenant and the workspace exists, so
        // an insufficient role is a 403. Exact, non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Find the target invitation WITHIN this tenant and workspace; an invitation id that belongs to another
        // workspace or tenant (or no invitation at all) is hidden as 404, never 403, so an invitation outside the
        // caller's resolved workspace can never be probed for (threats T1/T5). The by-id lookup never touches the
        // token secret.
        var invitation = await deps.WorkspaceInvitations
            .FindByIdAsync(context.OrganizationId, workspaceGuid, invitationGuid, cancellationToken)
            .ConfigureAwait(false);
        if (invitation is null)
        {
            return HiddenWorkspace();
        }

        // Only a PENDING invitation may be revoked: an already-accepted invitation must not silently undo a
        // granted membership, and an already-revoked one is a no-op. The caller is authorized; the lifecycle
        // state, not the caller, is the reason, so this is a 409 Conflict that changes nothing and writes no
        // duplicate audit fact. Placed AFTER the role check so a non-Owner/Admin still gets a 403 and never
        // learns the invitation state (threat T7). The domain Revoke() enforces the same invariant; checking
        // here keeps the rejection an HTTP 409 rather than a thrown exception.
        if (invitation.Status != WorkspaceInvitationStatus.Pending)
        {
            return InvitationNotPendingConflict();
        }

        // Capture the previous status name BEFORE the transition for the audit record (the in-memory object
        // survives, but capturing first keeps the audited "before" state independent of the mutation below).
        var previousStatus = invitation.Status.ToString();

        var now = timeProvider.GetUtcNow();
        invitation.Revoke(now);
        await deps.WorkspaceInvitations.UpdateAsync(invitation, cancellationToken).ConfigureAwait(false);

        // AUDIT: a revocation retires an outstanding invite token, so append an append-only audit record
        // capturing the actor (the admin who revoked it), the revoked invitation and the Pending -> Revoked
        // status transition (threat T6 revocation control). The invite token and the invited email are never
        // recorded — only the fact of the revocation (threats T6/T7).
        var entry = AuditLogEntry.ForMemberInvitationRevoked(
            context.OrganizationId,
            workspaceGuid,
            context.UserProfileId,
            nameof(WorkspaceInvitation),
            invitation.Id,
            previousStatus,
            invitation.Status.ToString(),
            now);
        await deps.AuditLog.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // The invitation's redeemability IS what the revoke takes away, and that is now persisted as the
        // Revoked status; nothing is returned (204 No Content), mirroring the member-removal sibling.
        return Results.NoContent();
    }

    // DELETE /api/v1/workspaces/{workspaceId}/members/{memberId}?organizationSlug={slug}
    private static async Task<IResult> RemoveWorkspaceMemberAsync(
        HttpContext httpContext,
        string workspaceId,
        string memberId,
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

        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed workspace or member id can never address a stored row; treat each as hidden (404),
        // never echoing back why.
        if (!Guid.TryParse(workspaceId, out var workspaceGuid) || workspaceGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        if (!Guid.TryParse(memberId, out var memberGuid) || memberGuid == Guid.Empty)
        {
            return HiddenWorkspace();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 (a foreign or non-existent tenant is
            // indistinguishable from a missing workspace; threat T5).
            return HiddenWorkspace();
        }

        var context = resolution.Context;

        // The target workspace must exist within the resolved tenant; otherwise hide as 404 (a cross-tenant
        // or unknown workspace is never revealed; threats T1/T5).
        var workspace = await deps.Workspaces
            .FindByIdAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return HiddenWorkspace();
        }

        // "Manage members" is Owner/Admin only (docs/06_AUTHORIZATION_MATRIX.md; csv/api_routes.csv roles
        // "Owner,Admin"), authorized by the caller's ORGANIZATION role exactly like the invite sibling on
        // this same path (POST .../members). The caller is a known member of the tenant and the workspace
        // exists, so an insufficient role is a 403. Exact, non-linear role check (no >/<).
        if (!(context.HasRole(MembershipRole.Owner) || context.HasRole(MembershipRole.Admin)))
        {
            return Forbidden();
        }

        // Find the target membership WITHIN this tenant and workspace; a member id that belongs to another
        // workspace or tenant (or no membership at all) is hidden as 404, never 403, so a member outside the
        // caller's resolved workspace can never be probed for (threats T1/T5).
        var target = await deps.WorkspaceMembers
            .FindByIdAsync(context.OrganizationId, workspaceGuid, memberGuid, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return HiddenWorkspace();
        }

        // Last-Owner invariant: the sole Owner of the workspace cannot be removed (a workspace must not be
        // left without an Owner). Only relevant when the target itself is an Owner; the count is
        // tenant- and workspace-scoped. This is an invariant conflict (409), not an authorization failure.
        if (target.HasRole(MembershipRole.Owner))
        {
            var ownerCount = await deps.WorkspaceMembers
                .CountByRoleAsync(context.OrganizationId, workspaceGuid, MembershipRole.Owner, cancellationToken)
                .ConfigureAwait(false);
            if (ownerCount <= 1)
            {
                return LastOwnerConflict();
            }
        }

        // Capture the removed identity/role BEFORE deletion for the audit record (the in-memory object
        // survives the delete, but capturing first keeps the audit independent of the tracked entity state).
        var removedMemberId = target.Id;
        var removedRole = target.Role;

        await deps.WorkspaceMembers.RemoveAsync(target, cancellationToken).ConfigureAwait(false);

        // AUDIT: removal is security-relevant access revocation, so append an append-only audit record
        // capturing the actor (the admin who removed the member), the removed membership and the revoked
        // role (threats T1/T6). The audit row outlives the now-deleted membership (recorded fact, not FK).
        var now = timeProvider.GetUtcNow();
        var entry = AuditLogEntry.ForMemberRemoval(
            context.OrganizationId,
            workspaceGuid,
            context.UserProfileId,
            nameof(WorkspaceMember),
            removedMemberId,
            removedRole.ToString(),
            now);
        await deps.AuditLog.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        // The membership row IS the access grant, so its deletion has already revoked the subject's access;
        // nothing is returned (204 No Content).
        return Results.NoContent();
    }

    /// <summary>
    /// Parses a generic membership role name (case-insensitive) and confirms it
    /// is a DEFINED role. The authorization matrix is non-linear, so this is a
    /// parse + defined check only; it never interprets the role into a
    /// capability and never compares roles by ordering. A null, blank, unknown
    /// or undefined value is rejected so an invitation can never grant an
    /// undefined role (threat T6 role limitation).
    /// </summary>
    private static bool TryParseRole(string? value, out MembershipRole role)
    {
        role = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Reject purely numeric input: only the stable role NAMES are accepted,
        // so a caller cannot smuggle an out-of-range numeric value past the
        // defined-check via Enum.TryParse's numeric path.
        if (int.TryParse(value, out _))
        {
            return false;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out role)
            && WorkspaceInvitation.IsValidRole(role);
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
        var workspaceInvitations = services.GetService<IWorkspaceInvitationRepository>();
        var quotaEnforcement = services.GetService<QuotaEnforcementService>();
        var auditLog = services.GetService<IAuditLogRepository>();
        var unitOfWork = services.GetService<TransactionalUnitOfWork>();

        if (resolver is null
            || workspaces is null
            || workspaceMembers is null
            || workspaceInvitations is null
            || quotaEnforcement is null
            || auditLog is null
            || unitOfWork is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new WorkspaceEndpointDependencies(
            resolver, workspaces, workspaceMembers, workspaceInvitations, quotaEnforcement, auditLog, unitOfWork);
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

    // A protected command was refused because it would exceed a server-enforced quota (docs/08: 409 conflict). The
    // detail names only the generic quota key (the same key the quota-status read returns, so a vertical can map it
    // to paywall copy) and never leaks an internal id or rationale (threat T7). The caller is authorized by role;
    // the limit, not the caller, is the reason, so this is a 409 rather than a 403.
    private static IResult QuotaExceeded(QuotaEnforcementDecision decision)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: $"This action would exceed the '{decision.EntitlementKey}' quota.");

    // The last Owner of a workspace cannot be removed: a workspace must not be
    // left without an Owner. The caller is authorized; the invariant, not the
    // caller, is the reason, so this is a 409 Conflict (docs/08_API_CONTRACTS.md).
    // The detail names only the generic invariant and leaks no tenant data
    // (threat T7).
    private static IResult LastOwnerConflict()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The last Owner of the workspace cannot be removed.");

    // The caller is already a member of the workspace, so redeeming an invitation grants nothing new and does
    // NOT consume the token. The caller is authorized (they hold a valid token for their own workspace); the
    // pre-existing standing, not the token, is the reason, so this is a 409 Conflict (docs/08_API_CONTRACTS.md).
    // The detail names only the generic condition and leaks no tenant data (threat T7).
    private static IResult AlreadyMemberConflict()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "You are already a member of this workspace.");

    // The invitation is not pending (already accepted or revoked), so it cannot be revoked: an accepted
    // invitation must not silently undo a granted membership and an already-revoked one is a no-op. The caller
    // is authorized; the lifecycle state, not the caller, is the reason, so this is a 409 Conflict
    // (docs/08_API_CONTRACTS.md). The detail names only the generic condition and leaks no tenant data (threat
    // T7) — it deliberately does not distinguish "accepted" from "revoked".
    private static IResult InvitationNotPendingConflict()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The invitation is not pending and cannot be revoked.");

    // The workspace is archived and therefore read-only (CORE-LIFE-009), so an authoring mutation (rename,
    // member invite) is refused. The caller is authorized; the lifecycle state, not the caller, is the
    // reason, so this is a 409 Conflict. The detail names only the generic state and leaks no tenant data
    // (threat T7).
    private static IResult ArchivedReadOnly()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The workspace is archived and is read-only.");

    // The workspace is already archived, so a second archive changes nothing (the archive is terminal). The
    // caller is authorized; the lifecycle state, not the caller, is the reason, so this is a 409 Conflict
    // (docs/08_API_CONTRACTS.md). The detail names only the generic state and leaks no tenant data (threat T7).
    private static IResult AlreadyArchivedConflict()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The workspace is already archived.");

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
        IWorkspaceMemberRepository WorkspaceMembers,
        IWorkspaceInvitationRepository WorkspaceInvitations,
        QuotaEnforcementService QuotaEnforcement,
        IAuditLogRepository AuditLog,
        TransactionalUnitOfWork UnitOfWork);

    /// <summary>
    /// The kind of outcome the atomic invitation-redeem unit of work produced (CORE-WS-006), mapped to an
    /// HTTP result by the accept handler. A struct discriminated by <see cref="AcceptInvitationOutcomeKind"/>
    /// rather than a thrown exception, so the transaction commits/rolls back on its own value and the handler
    /// stays free of control-flow exceptions.
    /// </summary>
    private enum AcceptInvitationOutcomeKind
    {
        /// <summary>Fail-closed: an unknown, expired, revoked, already-redeemed or foreign token (hidden 404).</summary>
        Rejected,

        /// <summary>The caller already held a membership in the workspace; the token was not consumed (409).</summary>
        AlreadyMember,

        /// <summary>The token was redeemed and the caller became a member (201).</summary>
        Redeemed,
    }

    /// <summary>
    /// Result of the atomic invitation-redeem unit of work (CORE-WS-006): a <see cref="AcceptInvitationOutcomeKind"/>
    /// plus, for a successful redemption, the created <see cref="WorkspaceMember"/>. The member is non-null
    /// only for <see cref="AcceptInvitationOutcomeKind.Redeemed"/>.
    /// </summary>
    private readonly record struct AcceptInvitationOutcome(AcceptInvitationOutcomeKind Kind, WorkspaceMember? Member)
    {
        public static AcceptInvitationOutcome Rejected() => new(AcceptInvitationOutcomeKind.Rejected, null);

        public static AcceptInvitationOutcome AlreadyMember() => new(AcceptInvitationOutcomeKind.AlreadyMember, null);

        public static AcceptInvitationOutcome Redeemed(WorkspaceMember member)
            => new(AcceptInvitationOutcomeKind.Redeemed, member);
    }
}
