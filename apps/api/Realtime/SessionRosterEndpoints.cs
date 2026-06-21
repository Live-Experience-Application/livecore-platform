// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.AspNetCore.Mvc;

namespace LiveCore.Api.Realtime;

/// <summary>
/// HTTP endpoint of the Realtime module's participant ROSTER + PRESENCE read (CORE-PRS-002, the "Vertical
/// Adopter Consumability Completeness" epic). Until this story there was no way to read WHO is in a session and
/// who is currently connected — the join/leave presence COMMANDS (CORE-PRS-001) emitted the durable presence
/// events, the <see cref="RealtimeConnectionRegistry"/> tracked the live connections and the Participants
/// module owned the records, but no route surfaced the roster, so a vertical UI that needs a "who is present"
/// panel could not get it (the PO finding). This story adds that read ON TOP of the existing
/// Participants/Realtime/Visibility building blocks, without a parallel roster engine.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/sessions/{sessionId}/roster</c> — module <b>Realtime</b>, roles "workspace members".
///   A tenant/workspace/session-scoped read of the session's participant roster with each participant's current
///   presence/connection state, ROLE-PROJECTED. The route path carries only the <c>{sessionId}</c>, so the
///   target tenant is the required <c>?organizationSlug=</c> query parameter, exactly like the recap read and
///   the reconnect-replay route.</item>
/// </list>
///
/// WHICH PARTICIPANTS. A participant is workspace-scoped and there is no persisted session-participant roster
/// (docs/11_REALTIME_SYNC.md), so a session's roster is its workspace's ACTIVE participants — the SAME audience
/// population a participant realtime connection is admitted from and an audience-wide reveal fans out to. It is
/// read through the reused <see cref="IParticipantRepository.ListActiveByWorkspaceAsync"/> (tenant- AND
/// workspace-scoped), never a parallel lookup; a soft-removed participant has left the audience and is
/// excluded. Each participant's <c>present</c> flag comes from
/// <see cref="RealtimeConnectionRegistry.GetConnectedParticipantIds"/> — true iff the participant currently
/// holds a live realtime connection to THIS session on this API instance (the cross-instance aggregation caveat
/// is documented on that method and in docs/11; it only ever under-reports presence, never widens access).
///
/// AUTHORIZATION (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md; threats T1/T5/T2), authorized
/// exactly like the recap read surface (any "workspace members" role may read, and the member's actual
/// workspace ROLE then drives the projection), load-then-authorize, fail-closed at every step and never leaking
/// why:
/// <list type="bullet">
///   <item>The principal is mapped fail-closed from the request's <see cref="System.Security.Claims.ClaimsPrincipal"/>;
///   a failed mapping is 401 (the route group also challenges an anonymous caller before any handler runs).</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session id and a denied tenant
///   resolution (a service account, a foreign/unclaimed/unknown tenant) are hidden as 404 — a caller who cannot
///   see the tenant must not learn whether the session exists (threat T5).</item>
///   <item>The session is loaded WITHIN the resolved tenant (<see cref="ISessionRepository.FindByIdInOrganizationAsync"/>,
///   leading with the organization id), so a session in another tenant is never returned even when the
///   surrogate id matches; a cross-tenant or unknown session is hidden as 404 (threats T1/T5). The session's
///   own workspace id is discovered from the loaded row AFTER the tenant boundary is enforced.</item>
///   <item>A caller who is not a member of the session's workspace is ALSO hidden as 404 (a non-member must not
///   learn the session exists; the same object-level rule as the recap read), never 403.</item>
///   <item>A known workspace member receives the roster PROJECTED BY ROLE through
///   <see cref="SessionRosterProjection"/>: the host-content roles (Owner/Admin/Host/CoHost, classified by the
///   central <see cref="Visibility.VisibilityRoles"/>) receive the full <see cref="SessionRosterView"/> WITH
///   the host-only participant user link; every other role (Participant/Observer/Auditor, and any undefined
///   value) falls closed to the host-only-field-stripped <see cref="ParticipantRosterView"/> — so a participant
///   sees only the roster projection allowed by visibility rules, never a host-only field (threat T2).</item>
/// </list>
///
/// There is deliberately no 403 path here: the read is allowed to ANY workspace member, and the only role
/// effect is the SHAPE of the response (host vs audience), so an unauthorized role does not lose access — it
/// loses the host-only fields. A caller with no standing at all is hidden as 404 (threats T1/T5/T2).
///
/// Persistence dependency: like the recap/replay endpoints, this uses the tenant context resolver and the
/// session, workspace-member and participant repositories, registered only when a database connection string
/// is configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503. The
/// <see cref="RealtimeConnectionRegistry"/> it reads presence from is registered unconditionally (it has no
/// database dependency).
/// </summary>
internal static class SessionRosterEndpoints
{
    /// <summary>Required query parameter naming the target organization (the tenant the session belongs to).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapSessionRosterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs. The session-scoped roster read shares the by-session-id
        // prefix with the lifecycle commands and the recap/replay reads; the full template differs, so the
        // modules can share the prefix without collision.
        var bySession = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        bySession.MapGet("/{sessionId}/roster", ReadRosterAsync);

        return endpoints;
    }

    // GET /api/v1/sessions/{sessionId}/roster?organizationSlug={slug}
    private static async Task<IResult> ReadRosterAsync(
        HttpContext httpContext,
        string sessionId,
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
        // organization, so it is a query parameter exactly like the by-session-id lifecycle commands.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; treat it as hidden (404), never echoing
        // back why.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenSession();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is indistinguishable
            // from a missing session (docs/08: "404 = not found or intentionally hidden"; threat T5).
            return HiddenSession();
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant. The lookup leads with the organization id, so a session
        // in another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown
        // session is hidden as 404 (threats T1/T5). The session's own workspace id is then discovered from the
        // loaded row, AFTER the tenant boundary has been enforced — the same shape as the recap/replay reads.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return HiddenSession();
        }

        // Object-level authorization: the caller must be a member of the SESSION'S own workspace. The read is
        // allowed to ANY membership role (the same "workspace members" rule as the recap read); a non-member is
        // hidden as 404 (not 403) so resource existence is not leaked (threats T1/T5). The member's actual
        // workspace ROLE then drives the host-vs-participant projection below, so this loads the membership row
        // scoped by organization id then the session's own workspace id — a control role held in a DIFFERENT
        // workspace never confers standing here.
        var member = await deps.WorkspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return HiddenSession();
        }

        // The roster is the session AUDIENCE: the workspace's ACTIVE participants (tenant- AND workspace-scoped,
        // reusing the existing repository — never a parallel lookup), in deterministic append order. A
        // soft-removed participant has left the audience and is excluded.
        var participants = await deps.Participants
            .ListActiveByWorkspaceAsync(context.OrganizationId, session.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        // Presence: the participant ids currently holding a live realtime connection to THIS session (scoped by
        // the full tenant/workspace/session tuple, this-instance only — see GetConnectedParticipantIds). The
        // registry is the same per-instance record eviction acts on, so the roster reflects active connections.
        var presentParticipantIds = deps.Connections.GetConnectedParticipantIds(
            context.OrganizationId, session.WorkspaceId, sessionGuid);

        // The caller's OWN participant in this session, resolved server-side from the SAME principal-to-participant
        // mapping the GET /sessions/{sessionId}/me self-resolution route uses (IParticipantRepository.FindByUserAsync,
        // tenant- AND workspace-scoped), never a client-supplied id (CORE-PSELF-001). It is null when the caller is
        // not itself a participant of the session (a host/observer with no participant record). It drives ONLY the
        // audience view's per-entry isSelf marker, computed by comparing each participant's surrogate id to the
        // caller's own — so the marker leaks no other participant's user id (threat T2/T7).
        var callerParticipant = await deps.Participants
            .FindByUserAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);

        // PROJECT BY ROLE: the host-content roles receive the full roster WITH the host-only participant user
        // link; every other role falls closed to the stripped audience roster WITHOUT it, so a participant sees
        // only the roster projection allowed by visibility rules (no host-only fields; threat T2). The caller's
        // own participant id stamps the audience view's isSelf marker.
        var response = SessionRosterProjection.Project(
            sessionGuid, participants, presentParticipantIds, member.Role, callerParticipant?.Id);
        return Results.Ok(response);
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. The tenant resolver and the
    /// session/workspace-member/participant repositories exist only when a database connection string is
    /// configured; when absent, the endpoint fails closed with 503 instead of throwing. The
    /// <see cref="RealtimeConnectionRegistry"/> is registered unconditionally (no database dependency), so it
    /// is always present.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out RosterEndpointDependencies dependencies)
    {
        var services = httpContext.RequestServices;
        var resolver = services.GetService<TenantContextResolver>();
        var sessions = services.GetService<ISessionRepository>();
        var workspaceMembers = services.GetService<IWorkspaceMemberRepository>();
        var participants = services.GetService<IParticipantRepository>();
        var connections = services.GetService<RealtimeConnectionRegistry>();

        if (resolver is null
            || sessions is null
            || workspaceMembers is null
            || participants is null
            || connections is null)
        {
            dependencies = default;
            return false;
        }

        dependencies = new RosterEndpointDependencies(
            resolver, sessions, workspaceMembers, participants, connections);
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
            detail: "Reading a session roster requires persistence, which is not configured.");

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

    // Session existence is hidden: a malformed session id, a session in a foreign or non-entitled tenant, an
    // unknown session, and a session in a workspace the caller does not belong to are ALL reported as 404, never
    // distinguishable from each other and never echoing the reason (docs/08; threats T1/T5).
    private static IResult HiddenSession()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct RosterEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        RealtimeConnectionRegistry Connections);
}
