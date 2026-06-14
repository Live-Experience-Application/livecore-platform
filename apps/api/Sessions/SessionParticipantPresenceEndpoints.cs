using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Sessions;

/// <summary>
/// HTTP endpoints of the participant PRESENCE flow (CORE-PRS-001) — the real entry point that finally makes the
/// documented <c>ParticipantJoined</c>/<c>ParticipantLeft</c> presence events fire end-to-end. CORE-EVT-002 built
/// the join/leave application services (<see cref="SessionParticipantJoinService"/> /
/// <see cref="SessionParticipantLeaveService"/>) that append and deliver those events, but left them with no
/// production caller; this story wires the missing caller as two HTTP routes, REUSING the existing services rather
/// than duplicating their object-level join/leave decision, their PII-free event emission, the
/// <c>session.participant.max</c> quota gate (CORE-MON-005) or the realtime eviction (CORE-RTC-002).
///
/// Routes owned here (csv/api_routes.csv), realizing the documented request flow "authentication middleware
/// -&gt; tenant/workspace context resolver -&gt; endpoint -&gt; authorization policy -&gt; domain service"
/// (docs/02_ARCHITECTURE.md), mirroring the session lifecycle routes in <see cref="SessionEndpoints"/> exactly:
/// <list type="bullet">
///   <item><c>POST /api/v1/sessions/{sessionId}/participants/{participantId}/join</c> — admits the participant
///   to the session, driving <see cref="SessionParticipantJoinService.JoinAsync"/>: it re-validates the
///   object-level join inside the (organization, workspace) boundary, atomically check-and-consumes the
///   session's <c>session.participant.max</c> quota (CORE-MON-005, the free-tier cap now enforced on this REAL
///   path), and on (and only on) an admission appends and delivers the durable, identifier-only
///   <c>ParticipantJoined</c> event to the session audience (the hosts, observers and active participants).</item>
///   <item><c>POST /api/v1/sessions/{sessionId}/participants/{participantId}/leave</c> — removes the participant,
///   driving <see cref="SessionParticipantLeaveService.LeaveAsync"/>: on (and only on) an actual departure it
///   soft-removes the participant, releases the participant-quota slot, appends and delivers the durable
///   <c>ParticipantLeft</c> event (the just-removed participant is excluded from the fan-out) and evicts any
///   still-open hub connection. A participant that had already left is an idempotent no-op (200) that emits no
///   second event.</item>
/// </list>
///
/// Authorization model — HOST-DRIVEN presence (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md;
/// threats T1/T5), load-then-authorize, fail-closed at every step and never leaking why. Managing who is present
/// in a session is a session-control action, so the routes are authorized to exactly the session-control roles
/// the start/end/cancel commands use — <c>Owner</c>, <c>Admin</c>, <c>Host</c>, <c>CoHost</c> — in the SESSION'S
/// OWN workspace (the recipients of the presence events are "Host/CoHost and configured audience",
/// docs/09_EVENT_CATALOG.md). The pipeline is identical to the lifecycle commands:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed mapping
///   is 401 (the route group also challenges an anonymous caller before any handler runs).</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session or participant id and a denied
///   tenant resolution are hidden as 404 (a caller who cannot see the tenant must not learn whether the session
///   exists; threat T5).</item>
///   <item>A session not present in the resolved tenant is hidden as 404; a caller who is not a member of the
///   session's workspace is ALSO hidden as 404 (a non-member must not learn the session exists; threats T1/T5),
///   never 403.</item>
///   <item>A known workspace member who lacks the session-control role is 403. <see cref="MembershipRole"/> is
///   non-linear, so the role check is EXACT (role == Owner || == Admin || == Host || == CoHost), never an
///   ordering comparison.</item>
/// </list>
///
/// Object-level participant decisions stay inside the reused services, so a participant outside the session's
/// tenant/workspace is hidden as 404 (the service's <c>ParticipantNotFound</c>), a removed participant cannot
/// join (409), an ended session cannot be joined (409) and a join that would exceed the participant cap is 409 —
/// the limit, not the caller, being the reason, exactly like the session-start quota gate. Every denial emits NO
/// event: an unauthorized, hidden or rejected command can never leak a presence event to the audience
/// (fail-closed; threats T1/T3/T5). The response is the identifier-only <see cref="ParticipantPresenceResponse"/>
/// — never a participant display name or any PII (threat T7).
///
/// Persistence dependency: like the session endpoints, these use the tenant context resolver, the session and
/// workspace-member repositories and the join/leave services, which are registered only when a database
/// connection string is configured (see <c>Program.cs</c>). When persistence is off the endpoints fail closed
/// with 503 rather than crashing startup.
/// </summary>
internal static class SessionParticipantPresenceEndpoints
{
    /// <summary>Required query parameter naming the target organization (the route path carries no organization).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapSessionParticipantPresenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs (the same group shape as the session lifecycle routes).
        var byId = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        byId.MapPost("/{sessionId}/participants/{participantId}/join", JoinAsync);
        byId.MapPost("/{sessionId}/participants/{participantId}/leave", LeaveAsync);

        return endpoints;
    }

    // POST /api/v1/sessions/{sessionId}/participants/{participantId}/join?organizationSlug={slug}
    private static async Task<IResult> JoinAsync(
        HttpContext httpContext,
        string sessionId,
        string participantId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(
            httpContext, sessionId, participantId, organizationSlug, cancellationToken).ConfigureAwait(false);
        if (authorization.Failure is { } failure)
        {
            return failure;
        }

        var command = authorization.Command;

        // Delegate the object-level join to the reused service: it re-validates the participant inside the
        // session's (organization, workspace) boundary, enforces the session.participant.max quota and, on (and
        // only on) an admission, appends + delivers the durable, identifier-only ParticipantJoined event.
        var result = await command.Join
            .JoinAsync(command.OrganizationId, command.WorkspaceId, command.SessionId, command.ParticipantId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Admitted)
        {
            return Results.Ok(new ParticipantPresenceResponse(command.SessionId, command.ParticipantId, "Joined"));
        }

        // Map the typed denial to a fail-closed response. The two not-found reasons hide existence as 404 (a
        // participant outside the session's tenant/workspace is indistinguishable from a missing one; threats
        // T1/T5); the lifecycle and quota reasons are 409 conflicts (the caller is authorized and the resources
        // are real and in scope — the participant/session state or the cap, not the caller, is the reason).
        return result.Reason switch
        {
            SessionJoinDenialReason.SessionNotFound => HiddenSession(),
            SessionJoinDenialReason.ParticipantNotFound => HiddenSession(),
            SessionJoinDenialReason.ParticipantNotActive => Conflict("The participant cannot join from its current state."),
            SessionJoinDenialReason.SessionNotJoinable => Conflict("The session cannot be joined from its current state."),
            SessionJoinDenialReason.QuotaExceeded => Conflict("This action would exceed the session participant quota."),

            // Unreachable: an admission returned above and None is reserved for it. Fail closed rather than
            // returning a misleading success.
            _ => ServiceUnavailable(),
        };
    }

    // POST /api/v1/sessions/{sessionId}/participants/{participantId}/leave?organizationSlug={slug}
    private static async Task<IResult> LeaveAsync(
        HttpContext httpContext,
        string sessionId,
        string participantId,
        [FromQuery(Name = _organizationSlugQuery)] string? organizationSlug,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(
            httpContext, sessionId, participantId, organizationSlug, cancellationToken).ConfigureAwait(false);
        if (authorization.Failure is { } failure)
        {
            return failure;
        }

        var command = authorization.Command;

        // Delegate the leave to the reused service: it soft-removes the participant inside the session's
        // (organization, workspace) boundary and, on (and only on) an actual departure, releases the
        // participant-quota slot, appends + delivers the durable ParticipantLeft event and evicts the connection.
        var result = await command.Leave
            .LeaveAsync(command.OrganizationId, command.WorkspaceId, command.SessionId, command.ParticipantId, cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            // A real departure: the participant left and the ParticipantLeft event was delivered.
            ParticipantLeaveOutcome.Left
                => Results.Ok(new ParticipantPresenceResponse(command.SessionId, command.ParticipantId, "Left")),

            // The participant had already left: an idempotent no-op (200) that emitted no second event, so the
            // desired end state — the participant is not present — already holds.
            ParticipantLeaveOutcome.AlreadyLeft
                => Results.Ok(new ParticipantPresenceResponse(command.SessionId, command.ParticipantId, "AlreadyLeft")),

            // A session or participant outside the session's tenant/workspace is hidden as 404 (threats T1/T5).
            ParticipantLeaveOutcome.SessionNotFound => HiddenSession(),
            ParticipantLeaveOutcome.ParticipantNotFound => HiddenSession(),

            // Unreachable: the outcomes above are exhaustive. Fail closed.
            _ => ServiceUnavailable(),
        };
    }

    /// <summary>
    /// Runs the shared fail-closed authorization pipeline for both presence verbs — dependencies, principal,
    /// organization, session/participant ids, tenant resolution, session load (to discover the session's
    /// workspace inside the tenant boundary) and the exact non-linear workspace-role check. Returns the resolved
    /// <see cref="AuthorizedPresenceCommand"/> when the caller is allowed, or a fail-closed
    /// <see cref="PresenceAuthorization.Failure"/> response (503/401/400/404/403) otherwise, so a verb handler
    /// only ever reaches the join/leave service for an authorized command. The pipeline is identical for join and
    /// leave because managing presence is a session-control action authorized to the same roles, exactly like the
    /// start/end/cancel commands.
    /// </summary>
    private static async Task<PresenceAuthorization> AuthorizeAsync(
        HttpContext httpContext,
        string sessionId,
        string participantId,
        string? organizationSlug,
        CancellationToken cancellationToken)
    {
        if (!TryGetDependencies(httpContext, out var deps))
        {
            return PresenceAuthorization.Denied(ServiceUnavailable());
        }

        if (!TryMapPrincipal(httpContext, out var principal))
        {
            return PresenceAuthorization.Denied(Unauthorized());
        }

        // The target organization is required and supplied by the request; the route path carries no
        // organization, so it is a query parameter exactly like the session start/end commands.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return PresenceAuthorization.Denied(MissingOrganization());
        }

        // A malformed session or participant id can never address a stored resource; treat it as hidden (404),
        // never echoing back why.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty
            || !Guid.TryParse(participantId, out var participantGuid) || participantGuid == Guid.Empty)
        {
            return PresenceAuthorization.Denied(HiddenSession());
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide the session as 404 so a foreign or non-existent tenant is
            // indistinguishable from a missing session (threat T5).
            return PresenceAuthorization.Denied(HiddenSession());
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant. The lookup leads with the organization id, so a session in
        // another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown session
        // is hidden as 404 (threats T1/T5). The session's own workspace id is then discovered from the loaded row,
        // AFTER the tenant boundary has been enforced — exactly the session lifecycle routes' pattern.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return PresenceAuthorization.Denied(HiddenSession());
        }

        // Object-level authorization: the caller must be a member of the SESSION'S workspace. A caller who is a
        // member of the tenant but NOT of the session's workspace must not learn the session exists, so a missing
        // membership is hidden as 404 (not 403) — the same rule as the session start/end routes (threats T1/T5).
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return PresenceAuthorization.Denied(HiddenSession());
        }

        // The caller is a known member of the session's workspace, so an insufficient role is a 403 (authorized
        // to know the session exists, but not to manage its participants). Managing presence is a session-control
        // action, so the roles are exactly the start/end/cancel set "Owner,Admin,Host,CoHost". MembershipRole is
        // non-linear, so this is an EXACT set membership check, never a >/< ordering comparison.
        if (!(member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost)))
        {
            return PresenceAuthorization.Denied(Forbidden());
        }

        // Authorized: carry the validated, tenant- and workspace-scoped ids and the reused services to the verb
        // handler, so it can never act on unauthorized or cross-tenant ids.
        return PresenceAuthorization.Allowed(new AuthorizedPresenceCommand(
            context.OrganizationId,
            session.WorkspaceId,
            sessionGuid,
            participantGuid,
            deps.Join,
            deps.Leave));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. They exist only when a database
    /// connection string is configured; when absent, the endpoint fails closed with 503 instead of throwing —
    /// exactly like the session lifecycle endpoints.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out PresenceEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var join = services.GetService<SessionParticipantJoinService>();
        var leave = services.GetService<SessionParticipantLeaveService>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || join is null
            || leave is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new PresenceEndpointDependencies(resolver, sessions, workspaceMembers, join, leave);
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
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Participant presence operations require persistence, which is not configured.");

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
        => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    // The presence command cannot be applied from the current state (a removed participant, an ended session) or
    // would exceed the participant cap. The detail names only the rejected transition / the generic quota, never
    // the resource's actual state or any internal id (docs/08; threat T7). The caller is authorized; the state or
    // the limit, not the caller, is the reason, so this is a 409 rather than a 403.
    private static IResult Conflict(string detail)
        => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: detail);

    // Session/participant existence is hidden: a malformed id, a session or participant in a foreign or
    // non-entitled tenant, an unknown one, and a session in a workspace the caller does not belong to are ALL
    // reported as 404, never distinguishable from each other and never echoing the reason (docs/08; threats
    // T1/T5).
    private static IResult HiddenSession()
        => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    /// <summary>
    /// Outcome of the shared authorization pipeline: either a fail-closed <see cref="Failure"/> response, or an
    /// authorized <see cref="Command"/> carrying the validated ids and reused services. Exactly one is set —
    /// <see cref="Failure"/> is non-null on a denial and null on success.
    /// </summary>
    private readonly record struct PresenceAuthorization(IResult? Failure, AuthorizedPresenceCommand Command)
    {
        public static PresenceAuthorization Denied(IResult failure) => new(failure, default);

        public static PresenceAuthorization Allowed(AuthorizedPresenceCommand command) => new(null, command);
    }

    /// <summary>
    /// The validated, tenant- and workspace-scoped context of an authorized presence command, carrying the four
    /// surrogate ids and the reused join/leave services. Minted only after the full fail-closed pipeline passes,
    /// so a verb handler can never act on unauthorized or cross-tenant ids.
    /// </summary>
    private readonly record struct AuthorizedPresenceCommand(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        Guid ParticipantId,
        SessionParticipantJoinService Join,
        SessionParticipantLeaveService Leave);

    private readonly record struct PresenceEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        SessionParticipantJoinService Join,
        SessionParticipantLeaveService Leave);
}
