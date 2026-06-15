using System.Security.Claims;
using System.Text.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Realtime;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Sessions;

/// <summary>
/// HTTP endpoints of the Sessions module. They realize the documented request flow
/// end-to-end for the session routes: "authentication middleware -> tenant/workspace
/// context resolver -> endpoint -> authorization policy" (docs/02_ARCHITECTURE.md),
/// mirroring <see cref="Workspaces.WorkspaceEndpoints"/> and
/// <see cref="Scenes.SceneEndpoints"/> exactly.
///
/// Routes owned here (csv/api_routes.csv), spanning two stories:
/// <list type="bullet">
///   <item><c>POST /api/v1/workspaces/{workspaceId}/sessions</c> (CORE-API-003) —
///   creates a new <see cref="SessionStatus.Prepared"/> session in the route's
///   workspace (drives <see cref="Session.Create"/>), authorized to the
///   session-control roles "Owner,Admin,Host,CoHost". The workspace's
///   <c>session.active.max</c> quota is enforced on create via the existing
///   <see cref="Entitlements.QuotaEnforcementService"/> (see <c>CreateSessionAsync</c>
///   for why the create CHECKS the quota but does not RECORD consumption — start does).
///   Rejected with 409 when the parent workspace is archived (read-only;
///   CORE-LIFE-009).</item>
///   <item><c>GET /api/v1/workspaces/{workspaceId}/sessions</c> (CORE-API-003) —
///   lists the route workspace's sessions (via
///   <see cref="ISessionRepository.ListByWorkspaceAsync"/>), authorized to "workspace
///   members" (any membership role). The list is "Filtered" to the caller's workspace
///   (it never crosses the tenant or workspace boundary); see <c>ListSessionsAsync</c>
///   for why there is a single generic projection rather than a host-vs-participant
///   split.</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/start</c> (CORE-SES-004) — starts the
///   session's live timeline (drives <see cref="Session.Start"/>), authorized to the
///   session-control roles "Host,CoHost,Owner,Admin". Emits a durable
///   <see cref="SessionEventTypes.SessionStarted"/> session event and appends a
///   <see cref="AuditAction.SessionStarted"/> audit record (CORE-EVT-001).</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/end</c> (CORE-SES-004) — ends the
///   session's live timeline (drives <see cref="Session.End"/>), authorized to the
///   same roles. Emits a durable <see cref="SessionEventTypes.SessionEnded"/> session
///   event and appends a <see cref="AuditAction.SessionEnded"/> audit record
///   (CORE-EVT-001).</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/cancel</c> (CORE-LIFE-010) — cancels a
///   not-yet-started (<see cref="SessionStatus.Prepared"/>) session (drives
///   <see cref="Session.Cancel"/>), authorized to the same session-control roles. A
///   SOFT, audited, terminal status transition (Prepared -&gt; Cancelled) — never a
///   hard delete — so the session's append-only <c>session_events</c> and
///   <c>audit_logs</c> history is preserved (the story note: "NEVER delete
///   append-only session_events or audit_logs - prefer a Cancelled status"). Any
///   non-Prepared state (live, ended, already-cancelled) is a 409 Conflict that
///   changes nothing and writes no audit fact. Appends a
///   <see cref="AuditAction.SessionCancelled"/> audit record.</item>
/// </list>
///
/// Authoritative behavior — the session STATUS TRANSITION, persisted, plus its
/// durable event and audit fact. The guarded Prepared -&gt; Live -&gt; Ended
/// transition on the <see cref="Session"/> aggregate is the authoritative state;
/// once it is persisted, the start/end commands honor the route table's "Emits
/// SessionStarted"/"Emits SessionEnded" annotation by publishing the matching
/// durable session event through <see cref="ISessionEventPublisher"/> (the ENDPOINT
/// publishes, exactly like the reveal command) and appending the matching
/// append-only audit record (CORE-EVT-001). The events are SUBJECTLESS audience
/// events (no visibility subject, no selected participant), so the recipient
/// resolver delivers each to the whole session audience — the hosts, the observers
/// and every active participant — and reconnect replay (CORE-RT-005) re-delivers
/// them. Because each successful transition emits exactly once, a start/end persists
/// exactly one event and one audit fact; a 409 out-of-state command (below) emits
/// neither. The cancel command instead appends only an audit record (its durable
/// event is not a documented catalog entry).
///
/// Tenant resolution (mirrors the workspace by-id routes): the route path carries
/// only <c>{sessionId}</c>, so the target organization is supplied by a required
/// <c>organizationSlug</c> QUERY parameter and turned into a trusted
/// <see cref="TenantContext"/> by <see cref="TenantContextResolver"/> (token
/// organization claim AND persisted organization membership — defence in depth,
/// threat T5). The session is then loaded WITHIN that resolved organization, the
/// caller's WORKSPACE membership in the session's own workspace is loaded, and the
/// command is authorized by that workspace role.
///
/// Authorization model (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5), load-then-authorize, fail-closed at every step and never
/// leaking why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's
///   <see cref="ClaimsPrincipal"/>; a failed mapping is 401.</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session id
///   and a denied tenant resolution are hidden as 404 (a caller who cannot see the
///   tenant must not learn whether the session exists; threat T5).</item>
///   <item>A session not present in the resolved tenant is hidden as 404; a caller
///   who is not a member of the session's workspace is ALSO hidden as 404 (a
///   non-member must not learn the session exists — the same object-level rule as
///   the workspace read-one route; threats T1/T5), never 403.</item>
///   <item>A known workspace member who lacks the session-control role is 403
///   (authorized to know the session exists in their workspace, but not to
///   start/end it). <see cref="MembershipRole"/> is non-linear, so the role check
///   is EXACT (role == Owner || == Admin || == Host || == CoHost), never an
///   ordering comparison.</item>
///   <item>An out-of-state transition (start a non-Prepared session, end a
///   non-Live session) is a 409 Conflict whose detail never leaks internal state
///   beyond "the session cannot be started/ended from its current state". This
///   409-on-invalid-transition also makes a retried command at-most-once for its
///   side effect (a re-start/re-end is rejected, not duplicated), so explicit
///   <c>Idempotency-Key</c> handling (docs/08_API_CONTRACTS.md; the
///   <c>idempotency_keys</c> table) is a follow-up rather than a gap.</item>
/// </list>
///
/// Persistence dependency: like the workspace endpoints, these use the
/// repositories and the tenant context resolver, which are registered only when a
/// database connection string is configured (see <c>Program.cs</c>). When
/// persistence is off the endpoints fail closed with 503 rather than crashing
/// startup.
/// </summary>
internal static class SessionEndpoints
{
    /// <summary>Required query parameter naming the target organization.</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so
        // a missing/invalid token is challenged as 401 before any handler runs.
        var byId = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        byId.MapPost("/{sessionId}/start", StartSessionAsync);
        byId.MapPost("/{sessionId}/end", EndSessionAsync);
        byId.MapPost("/{sessionId}/cancel", CancelSessionAsync);

        // Workspace-scoped create/list routes (CORE-API-003). The workspace is in the
        // path (exactly like the scene routes), so these live under the workspaces
        // prefix; the full templates differ from the scene templates, so the two
        // modules can share the prefix without collision.
        var workspaceScoped = endpoints
            .MapGroup("/api/v1/workspaces")
            .RequireAuthorization();

        workspaceScoped.MapGet("/{workspaceId}/sessions", ListSessionsAsync);
        workspaceScoped.MapPost("/{workspaceId}/sessions", CreateSessionAsync);

        return endpoints;
    }

    // GET /api/v1/workspaces/{workspaceId}/sessions?organizationSlug={slug}
    private static async Task<IResult> ListSessionsAsync(
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
        // scene list and the workspace by-id read.
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
        // The list is allowed to ANY membership role ("workspace members",
        // csv/api_routes.csv); a non-member is hidden as 404 (not 403) so resource
        // existence is not leaked — the same rule as the scene list and the workspace
        // read-one route (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The "Filtered" note on this route (csv/api_routes.csv) is the tenant- AND
        // workspace-scoping the repository enforces: the list never crosses the tenant
        // or workspace boundary, so a member only ever sees their own workspace's
        // sessions. Unlike the scene list, there is NO host-vs-participant projection
        // split: a session is a single generic resource with no hidden content to leak
        // (the SessionResponse carries only ids, the display title, the lifecycle status
        // and the server timestamps — the same safe DTO the start/end commands return,
        // docs/08; threat T7), so every workspace member receives the same projection.
        // Sessions themselves are not gated by visibility rules (those govern
        // scenes/content/entities, not the session aggregate); deciding which session
        // CONTENT an audience may see remains the Visibility module's concern.
        var sessions = await deps.Sessions
            .ListByWorkspaceAsync(context.OrganizationId, workspaceGuid, cancellationToken)
            .ConfigureAwait(false);

        var response = sessions.Select(SessionResponse.From).ToArray();
        return Results.Ok(response);
    }

    // POST /api/v1/workspaces/{workspaceId}/sessions
    private static async Task<IResult> CreateSessionAsync(
        HttpContext httpContext,
        string workspaceId,
        [FromBody] CreateSessionRequest? request,
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

        // Validate the session inputs before touching the tenant, so a broken body is a
        // 400 regardless of authorization. The rejected title is never echoed back
        // (threat T7).
        if (!Session.IsValidTitle(request.Title?.Trim()))
        {
            return ValidationError("A valid session title is required.");
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
        // same rule as the scene create and the workspace read-one route (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, workspaceGuid, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenWorkspace();
        }

        // The caller is a known member of the workspace, so an insufficient role is a
        // 403 (authorized to know the workspace exists, but not to create a session in
        // it). "Create session" is "Owner,Admin,Host,CoHost" (csv/api_routes.csv;
        // docs/06_AUTHORIZATION_MATRIX.md "Create session"). MembershipRole is
        // non-linear, so this is an EXACT set membership check, never a >/< ordering
        // comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Read-only when the parent workspace is archived (CORE-LIFE-009): creating a session is an authoring
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

        // Quota enforcement (CORE-API-003): creating a session is gated by the
        // workspace's session.active.max quota via the existing QuotaEnforcementService,
        // AFTER role authorization and BEFORE the session is created, computed entirely
        // server-side and fail-closed ("Free limits cannot be bypassed by clients";
        // docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The create only CHECKS the
        // quota (a READ-ONLY pre-flight, not the atomic consume); it deliberately does NOT
        // record consumption. session.active.max counts a
        // workspace's currently-LIVE sessions (it is consumed at start and released at
        // end, see RunLifecycleCommandAsync), and a created session is Prepared, not
        // live. Recording here would double-count when the session later starts and would
        // make the very session just created un-startable, so the active-session count
        // must remain owned by start/end. The check therefore means: a workspace already
        // running its maximum number of concurrent live sessions cannot create another
        // until it ends one. When no quota governs the deployment the create proceeds
        // unchanged.
        var quotaDecision = await deps.QuotaEnforcement
            .CheckAsync(
                EntitlementSubjectType.Workspace,
                workspaceGuid,
                QuotaEntitlementKeys.SessionActiveMax,
                amount: 1,
                cancellationToken)
            .ConfigureAwait(false);
        if (!quotaDecision.IsAllowed)
        {
            // Record the denial as a real audit fact (CORE-SPEC-002: AuditAction.QuotaExceeded). A tenant-scoped
            // fact: the caller (the audited actor) is denied for the workspace's session.active.max quota subject.
            await deps.AuditLog
                .AppendAsync(
                    AuditLogEntry.ForQuotaExceeded(
                        context.OrganizationId,
                        workspaceGuid,
                        context.UserProfileId,
                        nameof(EntitlementSubjectType.Workspace),
                        workspaceGuid,
                        timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaExceeded(quotaDecision);
        }

        // The injected TimeProvider stamps the creation timestamp, exactly like the
        // scene create and the lifecycle handlers. The session is created Prepared with
        // no live timeline (the state behind SessionCreated, docs/09_EVENT_CATALOG.md);
        // the durable SessionCreated event and its delivery belong to the later Session
        // Event Stream epic (CORE-EVT-001) and are deliberately NOT emitted here.
        var now = timeProvider.GetUtcNow();
        var session = Session.Create(context.OrganizationId, workspaceGuid, request.Title!.Trim(), now);

        // A session has no uniqueness constraint, so there is no 409 outcome to translate;
        // AddAsync always returns Added on success (a foreign-key violation would surface
        // as a DbUpdateException, which the membership check above already precludes for a
        // resolved, existing workspace).
        await deps.Sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);

        var response = SessionResponse.From(session);
        return Results.Created($"/api/v1/sessions/{session.Id}", response);
    }

    // POST /api/v1/sessions/{sessionId}/start?organizationSlug={slug}
    private static Task<IResult> StartSessionAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunLifecycleCommandAsync(
            httpContext,
            sessionId,
            organizationSlug,
            timeProvider,
            SessionLifecycleCommand.Start,
            cancellationToken);

    // POST /api/v1/sessions/{sessionId}/end?organizationSlug={slug}
    private static Task<IResult> EndSessionAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunLifecycleCommandAsync(
            httpContext,
            sessionId,
            organizationSlug,
            timeProvider,
            SessionLifecycleCommand.End,
            cancellationToken);

    // POST /api/v1/sessions/{sessionId}/cancel?organizationSlug={slug}
    private static Task<IResult> CancelSessionAsync(
        HttpContext httpContext,
        string sessionId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
        => RunLifecycleCommandAsync(
            httpContext,
            sessionId,
            organizationSlug,
            timeProvider,
            SessionLifecycleCommand.Cancel,
            cancellationToken);

    /// <summary>
    /// Shared handler for the start, end and cancel lifecycle commands. The three
    /// routes differ only in the transition they apply (and the conflict wording), so
    /// the fail-closed authorization pipeline — dependencies, principal, organization,
    /// session id, tenant resolution, session load, workspace membership load,
    /// exact non-linear role check — is factored here and runs identically for
    /// all three before <paramref name="command"/> applies the actual transition.
    /// All three share the same session-control roles (Owner/Admin/Host/CoHost), so the
    /// cancel command (CORE-LIFE-010) reuses this pipeline rather than duplicating it;
    /// it differs only in its terminal Prepared -&gt; Cancelled transition and its
    /// append-only audit record (the start/end durable events are the Realtime epic's
    /// concern and are still deferred).
    /// </summary>
    private static async Task<IResult> RunLifecycleCommandAsync(
        HttpContext httpContext,
        string sessionId,
        string? organizationSlug,
        TimeProvider timeProvider,
        SessionLifecycleCommand command,
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

        // The target organization is required and supplied by the request; the
        // route path carries no organization, so it is a query parameter exactly
        // like the workspace by-id reads.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; treat it as
        // hidden (404), never echoing back why.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenSession();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the session as 404 so a foreign or
            // non-existent tenant is indistinguishable from a missing session
            // (docs/08: "404 = not found or intentionally hidden"; threat T5).
            return HiddenSession();
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant. The lookup leads with the
        // organization id, so a session in another tenant is never returned even
        // when the surrogate id matches; a cross-tenant or unknown session is
        // hidden as 404 (threats T1/T5). The session's own workspace id is then
        // discovered from the loaded row, AFTER the tenant boundary has been
        // enforced.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return HiddenSession();
        }

        // Object-level authorization: the caller must be a member of the SESSION'S
        // workspace. A caller who is a member of the tenant but NOT of the
        // session's workspace must not learn the session exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the workspace
        // read-one route (threats T1/T5). The lookup is scoped by organization id
        // then workspace id.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenSession();
        }

        // The caller is a known member of the session's workspace, so an
        // insufficient role is a 403 (authorized to know the session exists in
        // their workspace, but not to start/end it). The session-control roles are
        // "Host,CoHost,Owner,Admin", taken authoritatively from csv/api_routes.csv
        // for the start/end routes (docs/06_AUTHORIZATION_MATRIX.md has no start/end
        // row; its "Create session" row is the closest analogue and grants the same
        // set). MembershipRole is non-linear, so this is an EXACT set membership
        // check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return Forbidden();
        }

        // Guard the transition BEFORE any transaction is opened — reads and the in-memory state-machine guard
        // only. CanStart/CanEnd/CanCancel guard the only legal predecessor state, so an out-of-state command is
        // a 409 Conflict (not a no-op and not a 5xx) that changes nothing. This 409-on-invalid-transition also
        // makes a retried command at-most-once for its side effect. The injected TimeProvider stamps the
        // transition timestamp, exactly like the workspace write handlers.
        //
        // Quota enforcement (CORE-ENTL-004; atomic per CORE-CONC-004): starting a session consumes one unit of the
        // session's WORKSPACE's session.active.max quota, and ending it releases that unit, so the quota reflects
        // the workspace's CURRENT count of live sessions. The atomic check-and-consume runs INSIDE the transaction
        // below (so the consume commits or rolls back with the transition) and is the single limit-guarded
        // statement that makes N concurrent starts at a limit of one yield exactly one success and N-1
        // quota-exceeded; it is computed entirely server-side and is fail-closed ("Free limits cannot be bypassed
        // by clients"; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). A rejected state guard returns here,
        // before the transaction.
        var now = timeProvider.GetUtcNow();
        switch (command)
        {
            case SessionLifecycleCommand.Start:
                if (!session.CanStart)
                {
                    return CannotStartConflict();
                }

                break;

            case SessionLifecycleCommand.End:
                if (!session.CanEnd)
                {
                    return CannotEndConflict();
                }

                break;

            case SessionLifecycleCommand.Cancel:
                // Cancel is valid ONLY from Prepared (a not-yet-started session): any other current state
                // (live, ended, already-cancelled) is a 409 Conflict that leaves the session unchanged and
                // writes no audit fact. A live session must be ended, not cancelled, so cancel never short-
                // circuits the active-session quota release that end owns.
                if (!session.CanCancel)
                {
                    return CannotCancelConflict();
                }

                break;

            default:
                // Unreachable: the enum has exactly the three commands above. Fail
                // closed rather than silently succeeding.
                return ServiceUnavailable();
        }

        // ONE unit of work (CORE-CONC-002): the guarded status transition, its quota change, its append-only
        // audit record and (for start/end) the append of its durable session event commit together in a
        // single database transaction — so a part-way failure (for example a crash after the state change but
        // before the event append) rolls them ALL back and the append-only event stream can never diverge
        // from the persisted session state. Because the emit happens only inside the committed transition,
        // each start/end persists exactly one event and one audit fact, and a 409 (rejected above) emits
        // neither. The cancel command (CORE-LIFE-010) appends only the audit record of the Prepared ->
        // Cancelled transition (its event is not a documented catalog entry). Realtime DELIVERY is held until
        // AFTER the commit (commit-then-publish, below), so a delivery failure cannot roll back committed
        // state.
        var outcome = await deps.UnitOfWork
            .ExecuteAsync(
                async transactionCancellationToken =>
                {
                    // CORE-CONC-005: RELOAD the session inside the unit of work so a RETRY (after a transient
                    // failure rolled the prior attempt back and the unit of work cleared the change tracker)
                    // re-applies the transition to FRESH persisted state — not the prior attempt's in-memory
                    // Prepared->Live mutation, which would otherwise throw InvalidSessionStateTransitionException
                    // on the second attempt. On the FIRST attempt the change tracker is untouched, so this returns
                    // the same row already loaded and authorized above (no behavior change). The session was
                    // authorized above and is never hard-deleted (cancel is a soft transition), so a null here is
                    // a genuine anomaly: fail closed rather than silently no-op.
                    var current = await deps.Sessions
                        .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, transactionCancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The authorized session was not found inside the unit of work.");

                    var events = new List<SessionEvent>();
                    switch (command)
                    {
                        case SessionLifecycleCommand.Start:
                            // ATOMIC check-and-consume FIRST (CORE-CONC-004): the session's WORKSPACE consumes one
                            // unit of its session.active.max quota in a single limit-guarded statement. If the
                            // consume is denied (the workspace is at its limit, including under a concurrent race),
                            // the transition is NOT applied and the command carries the denial out; because nothing
                            // was mutated, the (empty) transaction commits cleanly and the endpoint returns 409. A
                            // successful consume commits with the transition, so a rolled-back start never leaves a
                            // consumed slot behind.
                            var startDecision = await deps.QuotaEnforcement
                                .TryConsumeAsync(
                                    EntitlementSubjectType.Workspace,
                                    current.WorkspaceId,
                                    QuotaEntitlementKeys.SessionActiveMax,
                                    amount: 1,
                                    transactionCancellationToken)
                                .ConfigureAwait(false);
                            if (!startDecision.IsAllowed)
                            {
                                return new SessionLifecycleOutcome(startDecision, current, events);
                            }

                            // Capture the previous status name BEFORE the transition for the audit record, so
                            // the audited "before" state is independent of the mutation below.
                            var startPreviousStatus = current.Status.ToString();

                            current.Start(now);
                            await deps.Sessions.UpdateAsync(current, transactionCancellationToken).ConfigureAwait(false);

                            // AUDIT + EVENT (CORE-EVT-001): record the Prepared -> Live transition as an
                            // append-only audit fact and APPEND the durable SessionStarted session event.
                            await deps.AuditLog
                                .AppendAsync(
                                    AuditLogEntry.ForSessionStart(
                                        context.OrganizationId,
                                        current.WorkspaceId,
                                        context.UserProfileId,
                                        nameof(Session),
                                        current.Id,
                                        startPreviousStatus,
                                        current.Status.ToString(),
                                        now),
                                    transactionCancellationToken)
                                .ConfigureAwait(false);
                            events.Add(await AppendSessionLifecycleEventAsync(
                                deps, context, current, SessionEventTypes.SessionStarted, now, transactionCancellationToken)
                                .ConfigureAwait(false));
                            break;

                        case SessionLifecycleCommand.End:
                            // Capture the previous status name BEFORE the transition for the audit record.
                            var endPreviousStatus = current.Status.ToString();

                            current.End(now);
                            await deps.Sessions.UpdateAsync(current, transactionCancellationToken).ConfigureAwait(false);

                            // Ending a live session frees the workspace's active-session slot, so release the
                            // unit consumed at start (clamped at zero; a no-op when nothing was recorded).
                            await deps.QuotaEnforcement
                                .ReleaseAsync(
                                    EntitlementSubjectType.Workspace,
                                    current.WorkspaceId,
                                    QuotaEntitlementKeys.SessionActiveMax,
                                    amount: 1,
                                    transactionCancellationToken)
                                .ConfigureAwait(false);

                            // AUDIT + EVENT (CORE-EVT-001): record the Live -> Ended transition as an
                            // append-only audit fact and APPEND the durable SessionEnded session event.
                            await deps.AuditLog
                                .AppendAsync(
                                    AuditLogEntry.ForSessionEnd(
                                        context.OrganizationId,
                                        current.WorkspaceId,
                                        context.UserProfileId,
                                        nameof(Session),
                                        current.Id,
                                        endPreviousStatus,
                                        current.Status.ToString(),
                                        now),
                                    transactionCancellationToken)
                                .ConfigureAwait(false);
                            events.Add(await AppendSessionLifecycleEventAsync(
                                deps, context, current, SessionEventTypes.SessionEnded, now, transactionCancellationToken)
                                .ConfigureAwait(false));
                            break;

                        case SessionLifecycleCommand.Cancel:
                            // No quota interaction: session.active.max counts a workspace's currently-LIVE
                            // sessions, and a Prepared session has consumed none (start consumes, end
                            // releases), so cancelling one releases nothing — unlike end, cancel never touches
                            // the quota. Capture the previous status BEFORE the transition for the audit record.
                            var previousStatus = current.Status.ToString();

                            current.Cancel(now);
                            await deps.Sessions.UpdateAsync(current, transactionCancellationToken).ConfigureAwait(false);

                            // AUDIT: a cancel is a security-relevant lifecycle change, so append an append-only
                            // audit record capturing the actor (the host who cancelled it), the cancelled
                            // session and the Prepared -> Cancelled status transition (threats T1/T5). Unlike a
                            // deletion, a cancel records the before/after status because the session SURVIVES
                            // (a soft transition, like the workspace archive); the session's append-only
                            // session_events and audit_logs are preserved, never deleted. Cancel emits no
                            // durable catalog event, so it appends nothing to deliver.
                            var entry = AuditLogEntry.ForSessionCancellation(
                                context.OrganizationId,
                                current.WorkspaceId,
                                context.UserProfileId,
                                nameof(Session),
                                current.Id,
                                previousStatus,
                                current.Status.ToString(),
                                now);
                            await deps.AuditLog.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);
                            break;

                        default:
                            // Unreachable: guarded by the pre-transaction switch above.
                            throw new InvalidOperationException("Unsupported session lifecycle command.");
                    }

                    return new SessionLifecycleOutcome(QuotaDenial: null, current, events);
                },
                cancellationToken)
            .ConfigureAwait(false);

        // An over-quota start committed nothing (the transition was not applied), so the session is unchanged and
        // the command is a 409 — the limit, not the caller, is the reason (threat T7: the detail names only the
        // generic quota key). This is the race-loser path of the atomic consume above.
        if (outcome.QuotaDenial is { } denial)
        {
            // Record the denial as a real audit fact (CORE-SPEC-002: AuditAction.QuotaExceeded). The consume rolled
            // back with the (empty) transaction, so this append-only fact is written AFTER it, outside the unit of
            // work: a denied start changes nothing but is still audited. Tenant-scoped (the workspace subject).
            await deps.AuditLog
                .AppendAsync(
                    AuditLogEntry.ForQuotaExceeded(
                        context.OrganizationId,
                        session.WorkspaceId,
                        context.UserProfileId,
                        nameof(EntitlementSubjectType.Workspace),
                        session.WorkspaceId,
                        now),
                    cancellationToken)
                .ConfigureAwait(false);
            return QuotaExceeded(denial);
        }

        // COMMIT-THEN-PUBLISH (CORE-CONC-002): the transaction has committed, so deliver each appended
        // session event to the whole session audience OUTSIDE the transaction. Delivery is best-effort, so a
        // delivery failure cannot roll back the committed transition; reconnect replay re-delivers later
        // (CORE-RT-005). Cancel appended no event, so it delivers nothing.
        foreach (var sessionEvent in outcome.Events)
        {
            await deps.EventPublisher.DeliverAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        }

        // Build the response from the session the unit of work RELOADED and transitioned (CORE-CONC-005), so
        // it reflects the committed state even when the command was retried after a transient failure.
        return Results.Ok(SessionResponse.From(outcome.Session));
    }

    /// <summary>
    /// Composes the durable <c>SessionStarted</c>/<c>SessionEnded</c> session event for a lifecycle
    /// transition (CORE-EVT-001) and APPENDS it to the session's append-only stream INSIDE the command's
    /// unit-of-work transaction, returning the appended event so the endpoint delivers it AFTER the commit
    /// (commit-then-publish, CORE-CONC-002), matching the reveal command's pattern. The event is a SUBJECTLESS
    /// audience event — no visibility subject and no selected participant — so the recipient resolver (reused,
    /// not duplicated) delivers it UNCONDITIONALLY to the whole session audience: the session hosts, the
    /// observers and every active participant. The actor is the authenticated caller (the host who ran the
    /// command), and the payload carries session IDENTIFIERS only — the session id and the new lifecycle
    /// status name — never any content (threat T7). The append is the source of truth; reconnect replay
    /// (CORE-RT-005) re-delivers the event to a reconnecting client.
    /// </summary>
    private static async Task<SessionEvent> AppendSessionLifecycleEventAsync(
        SessionEndpointDependencies deps,
        TenantContext context,
        Session session,
        string eventType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new SessionLifecycleEventPayload(
            session.Id,
            session.Status.ToString()));

        var sessionEvent = SessionEvent.Create(
            context.OrganizationId,
            session.WorkspaceId,
            session.Id,
            eventType,
            context.UserProfileId,
            targetParticipantId: null,
            payload,
            schemaVersion: 1,
            now);

        await deps.EventPublisher.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        return sessionEvent;
    }

    /// <summary>
    /// The server-composed payload of a <c>SessionStarted</c>/<c>SessionEnded</c> event: the session id and
    /// its new lifecycle status name. Identifiers and a generic state name only — never any content
    /// (threat T7).
    /// </summary>
    private sealed record SessionLifecycleEventPayload(Guid SessionId, string Status);

    /// <summary>Which lifecycle transition a handler invocation applies.</summary>
    private enum SessionLifecycleCommand
    {
        Start = 1,
        End = 2,
        Cancel = 3,
    }

    /// <summary>
    /// The result of the lifecycle unit of work. Carries either a quota <see cref="QuotaDenial"/> (an over-quota
    /// start that applied no transition, so the committed transaction was empty and the endpoint returns 409) or
    /// the durable <see cref="Events"/> appended inside the transaction to deliver after the commit
    /// (commit-then-publish). The two are mutually exclusive: a denial carries no events. <see cref="Session"/>
    /// is the session instance the unit of work RELOADED and transitioned (CORE-CONC-005), so the response is
    /// built from the committed state rather than the instance loaded before the (possibly retried) transaction.
    /// </summary>
    private sealed record SessionLifecycleOutcome(
        QuotaEnforcementDecision? QuotaDenial,
        Session Session,
        IReadOnlyList<SessionEvent> Events);

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They
    /// exist only when a database connection string is configured; when absent, the
    /// endpoint fails closed with 503 instead of throwing.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out SessionEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaces = services.GetService<IWorkspaceRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var quotaEnforcement = services.GetService<QuotaEnforcementService>();
        var auditLog = services.GetService<IAuditLogRepository>();
        var eventPublisher = services.GetService<ISessionEventPublisher>();
        var unitOfWork = services.GetService<TransactionalUnitOfWork>();

        if (resolver is null
            || sessions is null
            || workspaces is null
            || workspaceMembers is null
            || quotaEnforcement is null
            || auditLog is null
            || eventPublisher is null
            || unitOfWork is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new SessionEndpointDependencies(
            resolver, sessions, workspaces, workspaceMembers, quotaEnforcement, auditLog, eventPublisher, unitOfWork);
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
            detail: "Session operations require persistence, which is not configured.");

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

    // Starting the session was refused because it would exceed a server-enforced quota (docs/08: 409 conflict). The
    // detail names only the generic quota key (the same key the workspace quota-status read returns, so a vertical
    // can map it to paywall copy) and never leaks an internal id or rationale (threat T7). The caller is authorized
    // by role; the limit, not the caller, is the reason, so this is a 409 rather than a 403.
    private static IResult QuotaExceeded(QuotaEnforcementDecision decision)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: $"This action would exceed the '{decision.EntitlementKey}' quota.");

    // The parent workspace is archived and therefore read-only (CORE-LIFE-009), so creating a session in it is
    // refused. The caller is authorized; the workspace's lifecycle state, not the caller, is the reason, so this
    // is a 409 Conflict. The detail names only the generic state and leaks no tenant data (threat T7).
    private static IResult ArchivedReadOnly()
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "The workspace is archived and is read-only.");

    private static IResult MissingOrganization()
        => ValidationError($"The '{_organizationSlugQuery}' value is required.");

    private static IResult ValidationError(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: detail);

    // The session cannot be started/ended from its current state. The detail names
    // only the rejected transition, never the session's actual status, so it leaks
    // no internal state beyond the fact that the command is not legal now
    // (docs/08; threat T7).
    private static IResult CannotStartConflict()
        => Conflict("The session cannot be started from its current state.");

    private static IResult CannotEndConflict()
        => Conflict("The session cannot be ended from its current state.");

    // The session cannot be cancelled from its current state: cancel is valid only from Prepared (a
    // not-yet-started session), so a live, ended or already-cancelled session is a 409. The detail names only
    // the rejected transition, never the session's actual status, so it leaks no internal state beyond the
    // fact that the command is not legal now (docs/08; threat T7).
    private static IResult CannotCancelConflict()
        => Conflict("The session cannot be cancelled from its current state.");

    private static IResult Conflict(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail);

    // Session existence is hidden: a malformed id, a session in a foreign or
    // non-entitled tenant, an unknown session, and a session in a workspace the
    // caller does not belong to are ALL reported as 404, never distinguishable from
    // each other and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenSession() => NotFound();

    // Workspace existence is hidden on the workspace-scoped create/list routes: a
    // malformed workspace id, a workspace in a foreign or non-entitled tenant, an
    // unknown workspace, and a workspace the caller does not belong to are ALL
    // reported as 404, never distinguishable and never echoing the reason — the same
    // rule the scene routes apply (docs/08; threats T1/T5).
    private static IResult HiddenWorkspace() => NotFound();

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SessionEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceRepository Workspaces,
        IWorkspaceMemberRepository WorkspaceMembers,
        QuotaEnforcementService QuotaEnforcement,
        IAuditLogRepository AuditLog,
        ISessionEventPublisher EventPublisher,
        TransactionalUnitOfWork UnitOfWork);
}
