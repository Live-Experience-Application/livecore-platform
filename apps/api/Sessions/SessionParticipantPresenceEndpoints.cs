// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Security.Claims;
using LiveCore.Api.Audit;
using LiveCore.Api.Entitlements;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
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
/// Authorization model — HOST admission OR self-service presence (object-level, server-side;
/// docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5), load-then-authorize, fail-closed at every step and never
/// leaking why. Two principals may record a participant's presence:
/// <list type="bullet">
///   <item>HOST admission — a session-control role (<c>Owner</c>, <c>Admin</c>, <c>Host</c>, <c>CoHost</c>) in
///   the SESSION'S OWN workspace may admit or remove ANY participant, exactly the role set the start/end/cancel
///   commands use (the recipients of the presence events are "Host/CoHost and configured audience",
///   docs/09_EVENT_CATALOG.md). This is the original, unchanged path.</item>
///   <item>SELF-SERVICE presence (CORE-PSELF-003, the "Participant Self-Service Lifecycle" epic) — the principal
///   that OWNS the target participant may record its OWN presence even without a control role, so a self-provisioned
///   audience member (CORE-PSELF-002) can join and leave with no host step. The owning principal is decided
///   server-side by matching the caller's resolved user profile to the target participant's <c>user_id</c>
///   (<see cref="Participant.BelongsToSubject"/>), never a client-supplied claim, so a caller can only ever record
///   its OWN presence and never another participant's.</item>
/// </list>
/// The pipeline is identical to the lifecycle commands, branching only on the authorization test:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="ClaimsPrincipal"/>; a failed mapping
///   is 401 (the route group also challenges an anonymous caller before any handler runs).</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session or participant id and a denied
///   tenant resolution are hidden as 404 (a caller who cannot see the tenant must not learn whether the session
///   exists; threat T5).</item>
///   <item>A session not present in the resolved tenant is hidden as 404; a caller who is not a member of the
///   session's workspace is ALSO hidden as 404 (a non-member must not learn the session exists; threats T1/T5),
///   never 403 — so a foreign-tenant or non-member caller stays a fail-closed 404 even on the self-service path.</item>
///   <item>A workspace member with a session-control role is authorized as a HOST. <see cref="MembershipRole"/> is
///   non-linear, so the role check is EXACT (role == Owner || == Admin || == Host || == CoHost), never an
///   ordering comparison.</item>
///   <item>A workspace member WITHOUT a control role is authorized for SELF-SERVICE only: the target participant
///   is loaded WITHIN the session's (organization, workspace) boundary, a participant outside that boundary is
///   hidden as 404 (indistinguishable from a missing one; threats T1/T5), and a member targeting a participant it
///   does NOT own is an unrelated caller denied 403.</item>
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

        // CORE-SPEC-002: a participant-quota denial is a real audit fact (AuditAction.QuotaExceeded, the catalog's
        // QuotaExceeded event). Record it before mapping the typed denial to the response; only the quota reason is
        // a quota event (the not-found and lifecycle reasons are not). It is a tenant-scoped fact: the caller (the
        // audited actor) is denied for the session's session.participant.max quota subject.
        if (result.Reason == SessionJoinDenialReason.QuotaExceeded)
        {
            await command.AuditLog
                .AppendAsync(
                    AuditLogEntry.ForQuotaExceeded(
                        command.OrganizationId,
                        command.WorkspaceId,
                        command.ActorUserProfileId,
                        nameof(EntitlementSubjectType.Session),
                        command.SessionId,
                        command.Clock.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
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
    /// workspace inside the tenant boundary) and the host-OR-self authorization test. Returns the resolved
    /// <see cref="AuthorizedPresenceCommand"/> when the caller is allowed, or a fail-closed
    /// <see cref="PresenceAuthorization.Failure"/> response (503/401/400/404/403) otherwise, so a verb handler
    /// only ever reaches the join/leave service for an authorized command. The pipeline is identical for join and
    /// leave: a session-control role may manage ANY participant (host admission), while the participant's OWN
    /// principal may record only its own presence (self-service, CORE-PSELF-003).
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
        // This is enforced for BOTH the host and the self-service paths, so a foreign-tenant or non-member caller
        // stays a fail-closed 404 even when it claims to own the participant.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return PresenceAuthorization.Denied(HiddenSession());
        }

        // HOST admission: a session-control role may manage ANY participant's presence. Managing presence is a
        // session-control action, so the roles are exactly the start/end/cancel set "Owner,Admin,Host,CoHost".
        // MembershipRole is non-linear, so this is an EXACT set membership check, never a >/< ordering comparison.
        var isSessionController = member.HasRole(MembershipRole.Owner)
            || member.HasRole(MembershipRole.Admin)
            || member.HasRole(MembershipRole.Host)
            || member.HasRole(MembershipRole.CoHost);

        if (!isSessionController)
        {
            // SELF-SERVICE presence (CORE-PSELF-003): a workspace member WITHOUT a control role may still record
            // its OWN presence, but only for the participant it owns. Load the target participant WITHIN the
            // session's (organization, workspace) boundary so the ownership test is tenant/workspace-safe; the
            // lookup leads with the organization id, so a participant in another tenant or workspace is never
            // returned (threats T1/T5). The reused JoinAsync/LeaveAsync service re-loads and re-validates the
            // participant, so this load decides ONLY authorization, never the object-level command itself.
            var participant = await deps.Participants
                .FindByIdAsync(context.OrganizationId, session.WorkspaceId, participantGuid, cancellationToken)
                .ConfigureAwait(false);
            if (participant is null)
            {
                // The participant does not exist in the session's workspace/tenant: hidden as 404,
                // indistinguishable from a missing one, never echoing why (threats T1/T5).
                return PresenceAuthorization.Denied(HiddenSession());
            }

            // The caller is a known member of the session's workspace (so it may learn the session and its
            // participants exist — the roster already lists them), but it neither holds a control role nor owns the
            // target participant. It is an unrelated caller trying to control another participant's presence, so it
            // is denied 403 (authorized to know they exist, not to act on another's behalf). The ownership match is
            // server-resolved from the caller's own user profile, never a client-supplied claim, so a caller can
            // only ever record ITS OWN presence (threats T1/T5).
            if (!participant.BelongsToSubject(context.UserProfileId))
            {
                return PresenceAuthorization.Denied(Forbidden());
            }
        }

        // Authorized — as a host (any participant) or as the participant's own principal (self-service). Carry the
        // validated, tenant- and workspace-scoped ids and the reused services to the verb handler, so it can never
        // act on unauthorized or cross-tenant ids.
        return PresenceAuthorization.Allowed(new AuthorizedPresenceCommand(
            context.OrganizationId,
            session.WorkspaceId,
            sessionGuid,
            participantGuid,
            context.UserProfileId,
            deps.Join,
            deps.Leave,
            deps.AuditLog,
            deps.Clock));
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
        var participants = services.GetService<IParticipantRepository>();
        var join = services.GetService<SessionParticipantJoinService>();
        var leave = services.GetService<SessionParticipantLeaveService>();
        var auditLog = services.GetService<IAuditLogRepository>();
        var clock = services.GetService<TimeProvider>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || participants is null
            || join is null
            || leave is null
            || auditLog is null
            || clock is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new PresenceEndpointDependencies(
            resolver, sessions, workspaceMembers, participants, join, leave, auditLog, clock);
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
            detail: "Participant presence operations require persistence, which is not configured.");

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

    private static IResult MissingOrganization()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status400BadRequest,
            code: ProblemCodes.ValidationError,
            title: "Bad Request",
            detail: $"The '{_organizationSlugQuery}' value is required.");

    // The presence command cannot be applied from the current state (a removed participant, an ended session) or
    // would exceed the participant cap. The detail names only the rejected transition / the generic quota, never
    // the resource's actual state or any internal id (docs/08; threat T7). The caller is authorized; the state or
    // the limit, not the caller, is the reason, so this is a 409 rather than a 403.
    private static IResult Conflict(string detail)
        => CoreProblem.Create(
            statusCode: StatusCodes.Status409Conflict,
            code: ProblemCodes.Conflict,
            title: "Conflict",
            detail: detail);

    // Session/participant existence is hidden: a malformed id, a session or participant in a foreign or
    // non-entitled tenant, an unknown one, and a session in a workspace the caller does not belong to are ALL
    // reported as 404, never distinguishable from each other and never echoing the reason (docs/08; threats
    // T1/T5).
    private static IResult HiddenSession()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
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
        Guid ActorUserProfileId,
        SessionParticipantJoinService Join,
        SessionParticipantLeaveService Leave,
        IAuditLogRepository AuditLog,
        TimeProvider Clock);

    private readonly record struct PresenceEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        SessionParticipantJoinService Join,
        SessionParticipantLeaveService Leave,
        IAuditLogRepository AuditLog,
        TimeProvider Clock);
}
