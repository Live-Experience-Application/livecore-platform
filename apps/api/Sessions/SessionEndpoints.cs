using System.Security.Claims;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Sessions;

/// <summary>
/// HTTP endpoints of the Sessions module's lifecycle commands (CORE-SES-004:
/// "Implement session start/end commands"). They realize the documented request
/// flow end-to-end for the two by-session-id routes: "authentication middleware
/// -> tenant/workspace context resolver -> endpoint -> authorization policy"
/// (docs/02_ARCHITECTURE.md), mirroring <see cref="Workspaces.WorkspaceEndpoints"/>
/// exactly.
///
/// Routes owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>POST /api/v1/sessions/{sessionId}/start</c> — starts the session's
///   live timeline (drives <see cref="Session.Start"/>), authorized to the
///   session-control roles "Host,CoHost,Owner,Admin".</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/end</c> — ends the session's live
///   timeline (drives <see cref="Session.End"/>), authorized to the same roles.</item>
/// </list>
///
/// Authoritative behavior — the session STATUS TRANSITION, persisted. The route
/// table annotates these commands "Emits SessionStarted"/"Emits SessionEnded",
/// but EVENT EMISSION IS DEFERRED to the Realtime epic and is deliberately NOT
/// done here: csv/database_tables.csv assigns the <c>session_events</c> table to
/// the Realtime module, and "event append and delivery" is CORE-RT-003. With no
/// event store and no SignalR transport yet there is nowhere to emit, so the
/// durable <c>SessionStarted</c>/<c>SessionEnded</c> events
/// (docs/09_EVENT_CATALOG.md) and their delivery are a follow-up. The
/// authoritative, persisted behavior of these endpoints is the guarded
/// Prepared -&gt; Live -&gt; Ended transition on the <see cref="Session"/>
/// aggregate (the state behind those events); the "Emits ..." annotation
/// describes the eventual emission these commands will drive.
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
        var group = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        group.MapPost("/{sessionId}/start", StartSessionAsync);
        group.MapPost("/{sessionId}/end", EndSessionAsync);

        return endpoints;
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

    /// <summary>
    /// Shared handler for the start and end lifecycle commands. The two routes
    /// differ only in the transition they apply (and the conflict wording), so the
    /// fail-closed authorization pipeline — dependencies, principal, organization,
    /// session id, tenant resolution, session load, workspace membership load,
    /// exact non-linear role check — is factored here and runs identically for
    /// both before <paramref name="command"/> applies the actual transition.
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

        // Apply the transition. The state machine is the authoritative behavior:
        // CanStart/CanEnd guard the only legal predecessor state, so an
        // out-of-state command is a 409 Conflict (not a no-op and not a 5xx). The
        // injected TimeProvider stamps the live-timeline boundary, exactly like the
        // workspace write handlers. The durable SessionStarted/SessionEnded events
        // these commands will eventually emit are deferred to the Realtime epic
        // (there is no event store/transport yet); the persisted status transition
        // is the behavior delivered here.
        //
        // Quota enforcement (CORE-ENTL-004): starting a session consumes one unit of the session's WORKSPACE's
        // session.active.max quota, and ending it releases that unit, so the quota reflects the workspace's CURRENT
        // count of live sessions. The check runs AFTER role authorization and the state guard, and BEFORE the
        // transition is persisted; it is computed entirely server-side and is fail-closed, so a free workspace
        // cannot run more concurrent sessions than its plan allows ("Free limits cannot be bypassed by clients";
        // docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). When no quota governs the deployment the command
        // proceeds unchanged.
        var now = timeProvider.GetUtcNow();
        switch (command)
        {
            case SessionLifecycleCommand.Start:
                if (!session.CanStart)
                {
                    return CannotStartConflict();
                }

                var quotaDecision = await deps.QuotaEnforcement
                    .CheckAsync(
                        EntitlementSubjectType.Workspace,
                        session.WorkspaceId,
                        QuotaEntitlementKeys.SessionActiveMax,
                        amount: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!quotaDecision.IsAllowed)
                {
                    return QuotaExceeded(quotaDecision);
                }

                session.Start(now);
                await deps.Sessions.UpdateAsync(session, cancellationToken).ConfigureAwait(false);

                // Record the consumption only after the start is persisted, so a failed start never increments the
                // count.
                await deps.QuotaEnforcement
                    .RecordConsumptionAsync(
                        EntitlementSubjectType.Workspace,
                        session.WorkspaceId,
                        QuotaEntitlementKeys.SessionActiveMax,
                        amount: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case SessionLifecycleCommand.End:
                if (!session.CanEnd)
                {
                    return CannotEndConflict();
                }

                session.End(now);
                await deps.Sessions.UpdateAsync(session, cancellationToken).ConfigureAwait(false);

                // Ending a live session frees the workspace's active-session slot, so release the unit consumed at
                // start (clamped at zero; a no-op when nothing was recorded).
                await deps.QuotaEnforcement
                    .ReleaseAsync(
                        EntitlementSubjectType.Workspace,
                        session.WorkspaceId,
                        QuotaEntitlementKeys.SessionActiveMax,
                        amount: 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                // Unreachable: the enum has exactly the two commands above. Fail
                // closed rather than silently succeeding.
                return ServiceUnavailable();
        }

        return Results.Ok(SessionResponse.From(session));
    }

    /// <summary>Which lifecycle transition a handler invocation applies.</summary>
    private enum SessionLifecycleCommand
    {
        Start = 1,
        End = 2,
    }

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
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var quotaEnforcement = services.GetService<QuotaEnforcementService>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || quotaEnforcement is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new SessionEndpointDependencies(resolver, sessions, workspaceMembers, quotaEnforcement);
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

    private static IResult NotFound()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SessionEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        QuotaEnforcementService QuotaEnforcement);
}
