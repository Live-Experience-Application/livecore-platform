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
/// HTTP endpoint of the Realtime module's participant SELF-RESOLUTION read (CORE-PSELF-001, the "Participant
/// Self-Identification" epic). The server already maps a principal to its participant
/// (<see cref="IParticipantRepository.FindByUserAsync"/>) but exposed no route for a participant to learn its OWN
/// surrogate participant id in-scope — <c>GET /api/v1/me</c> returns only the user profile plus organization
/// memberships, no participations — so an audience surface could not call the participant-keyed reads
/// (<c>getParticipantVisibleFeed</c>, the roster) for itself unless the host passed the surrogate participant id
/// out of band (the vertical-adopter finding ARC-GAP-007). This story adds that self-resolution route ON TOP of
/// the existing Participants/Sessions/Realtime building blocks, without a parallel mapping.
///
/// Route owned by this story (csv/api_routes.csv):
/// <list type="bullet">
///   <item><c>GET /api/v1/sessions/{sessionId}/me</c> — module <b>Realtime</b>, roles "Participant (self)".
///   A tenant/workspace/session-scoped read of the CALLER'S OWN participant context (its participant id, display
///   name and presence) for the session. The route path carries only the <c>{sessionId}</c>, so the target
///   tenant is the required <c>?organizationSlug=</c> query parameter, exactly like the roster and the
///   recap/replay reads.</item>
/// </list>
///
/// SELF-ONLY, SERVER-RESOLVED. The caller's participant is resolved ENTIRELY server-side from the authenticated
/// principal via <see cref="IParticipantRepository.FindByUserAsync"/> (the unique <c>(workspace_id, user_id)</c>
/// index guarantees at most one), NEVER a client-supplied participant id, so a caller can only ever resolve
/// ITSELF and never address another participant (threats T1/T5). The response carries only the caller's own
/// identity — never another participant and no host-only field.
///
/// AUTHORIZATION (object-level, server-side; docs/06_AUTHORIZATION_MATRIX.md "View own participant feed" /
/// "Resolve own session participant context"; threats T1/T5/T7), load-then-authorize, fail-closed at every step
/// and never leaking why:
/// <list type="bullet">
///   <item>The route group requires authorization, so an anonymous caller is challenged 401 before any handler
///   runs; the principal is then mapped fail-closed from the request's claims — a failed mapping is 401, and the
///   reason is never echoed (threat T7).</item>
///   <item>A missing/blank <c>organizationSlug</c> is 400; a malformed session id and a denied tenant resolution
///   (a service account, a foreign/unclaimed/unknown tenant) are hidden as 404 — a caller who cannot see the
///   tenant must not learn whether the session exists (threat T5).</item>
///   <item>The session is loaded WITHIN the resolved tenant (<see cref="ISessionRepository.FindByIdInOrganizationAsync"/>,
///   leading with the organization id), so a session in another tenant is never returned even when the surrogate
///   id matches; a cross-tenant or unknown session is hidden as 404 (threats T1/T5). The session's own workspace
///   id is discovered from the loaded row AFTER the tenant boundary is enforced.</item>
///   <item>The caller's own participant is then resolved within that tenant and the session's workspace. When no
///   participant exists yet, this read SELF-PROVISIONS one on the FIRST session-self read (CORE-PSELF-002) —
///   but ONLY when the caller is a member of the session's OWN workspace
///   (<see cref="IWorkspaceMemberRepository.IsMemberAsync"/>): the participant is created via
///   <see cref="Participant.Create"/> linked to the caller's own resolved user profile (never a client-supplied
///   id) with the token subject as the initial display name, through the Participants-owned
///   <see cref="IParticipantRepository.AddAsync"/>, idempotent on the unique (workspace_id, user_id) index, so a
///   re-call returns the SAME stable participant id and never a second participant. Authorization is NOT
///   loosened: a tenant member who is NOT a member of that workspace, and a foreign/unknown caller, both stay a
///   fail-closed 404 (never 403), so a non-member cannot probe for the session's existence (threats T1/T5).
///   A REMOVED participant holds no standing (it has left the audience, exactly as the roster excludes it and the
///   visible-feed hides it): one already exists for the caller, so it is not re-provisioned and is likewise
///   hidden as 404.</item>
/// </list>
///
/// Persistence dependency: like the roster/recap/replay endpoints, this uses the tenant context resolver and the
/// session, workspace-member and participant repositories, registered only when a database connection string is
/// configured (see <c>Program.cs</c>); when persistence is off the endpoint fails closed with 503. The
/// <see cref="RealtimeConnectionRegistry"/> it reads presence from is registered unconditionally (no database
/// dependency). The <see cref="TimeProvider"/> that stamps a self-provisioned participant is registered
/// unconditionally too.
/// </summary>
internal static class SessionParticipantSelfEndpoints
{
    /// <summary>Required query parameter naming the target organization (the tenant the session belongs to).</summary>
    private const string _organizationSlugQuery = "organizationSlug";

    public static IEndpointRouteBuilder MapSessionParticipantSelfEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Authenticated group: the bearer middleware authenticates the caller, so a missing/invalid token is
        // challenged as 401 before any handler runs. The self-resolution read shares the by-session-id prefix with
        // the lifecycle commands and the roster/recap/replay reads; the full template differs, so the modules can
        // share the prefix without collision.
        var bySession = endpoints
            .MapGroup("/api/v1/sessions")
            .RequireAuthorization();

        bySession.MapGet("/{sessionId}/me", ResolveOwnParticipantAsync);

        return endpoints;
    }

    // GET /api/v1/sessions/{sessionId}/me?organizationSlug={slug}
    private static async Task<IResult> ResolveOwnParticipantAsync(
        HttpContext httpContext,
        string sessionId,
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
        // organization, so it is a query parameter exactly like the roster and the by-session-id lifecycle
        // commands.
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return MissingOrganization();
        }

        // A malformed session id can never address a stored session; treat it as hidden (404), never echoing back
        // why.
        if (!Guid.TryParse(sessionId, out var sessionGuid) || sessionGuid == Guid.Empty)
        {
            return HiddenParticipant();
        }

        var resolution = await deps.Resolver
            .ResolveAsync(principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            // Not entitled to the tenant: hide as 404 so a foreign or non-existent tenant is indistinguishable
            // from a missing participant/session (docs/08: "404 = not found or intentionally hidden"; threat T5).
            return HiddenParticipant();
        }

        var context = resolution.Context;

        // Load the session WITHIN the resolved tenant. The lookup leads with the organization id, so a session in
        // another tenant is never returned even when the surrogate id matches; a cross-tenant or unknown session
        // is hidden as 404 (threats T1/T5). The session's own workspace id is then discovered from the loaded row,
        // AFTER the tenant boundary has been enforced — the same shape as the roster/recap/replay reads.
        var session = await deps.Sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionGuid, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return HiddenParticipant();
        }

        // Resolve the CALLER'S OWN participant — server-side, from the authenticated principal via the existing
        // principal-to-participant mapping, scoped by the resolved tenant and the session's own workspace. Never a
        // client-supplied id, so the caller can only ever resolve itself.
        var participant = await deps.Participants
            .FindByUserAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null)
        {
            // FIRST session-self read by a caller who has no participant yet: SELF-PROVISION one (CORE-PSELF-002)
            // when — and ONLY when — the caller is a member of the session's OWN workspace. A tenant member who is
            // not a member of that workspace, and a foreign/unknown caller, both stay a fail-closed 404 (never 403),
            // so a non-member cannot probe for the session's existence and authorization is NOT loosened (threats
            // T1/T5). The created participant is linked to the caller's own resolved user profile, so the caller can
            // only ever provision ITSELF.
            participant = await SelfProvisionParticipantAsync(
                    deps, principal, context, session.WorkspaceId, timeProvider, cancellationToken)
                .ConfigureAwait(false);
            if (participant is null)
            {
                return HiddenParticipant();
            }
        }

        // A removed participant holds no standing — it has left the session audience, exactly as the roster
        // excludes a soft-removed participant and the visible feed hides it — so its context is not served and is
        // hidden as 404 like every other denial.
        if (participant.Status != ParticipantStatus.Active)
        {
            return HiddenParticipant();
        }

        // Presence: whether the caller currently holds a live realtime connection to THIS session, from the same
        // per-instance registry (scoped by the full tenant/workspace/session tuple) the roster's presence flag
        // reads. It only ever under-reports across replicas, never widening access (docs/11).
        var present = deps.Connections
            .GetConnectedParticipantIds(context.OrganizationId, session.WorkspaceId, sessionGuid)
            .Contains(participant.Id);

        return Results.Ok(new SessionParticipantContext(
            sessionGuid, participant.Id, participant.DisplayName, present));
    }

    /// <summary>
    /// Resolves the persistence-backed dependencies from the request scope. The tenant resolver and the
    /// session/workspace-member/participant repositories exist only when a database connection string is
    /// configured; when absent, the endpoint fails closed with 503 instead of throwing. The
    /// <see cref="RealtimeConnectionRegistry"/> is registered unconditionally (no database dependency), so it is
    /// always present.
    /// </summary>
    private static bool TryGetDependencies(HttpContext httpContext, out SelfEndpointDependencies dependencies)
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

        dependencies = new SelfEndpointDependencies(resolver, sessions, workspaceMembers, participants, connections);
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

    /// <summary>
    /// Self-provisions the caller's OWN participant in the session's workspace on the first session-self read
    /// (CORE-PSELF-002), or returns <see langword="null"/> when the caller is not entitled to one (the caller
    /// then returns a fail-closed hidden 404). A participant is created ONLY when the caller is a member of the
    /// session's OWN workspace (<see cref="IWorkspaceMemberRepository.IsMemberAsync"/>, the SAME object-level
    /// membership check the roster read uses): a tenant member who is not a member of that workspace, and a
    /// foreign/unknown caller, both yield <see langword="null"/>, so authorization is NOT loosened (threats
    /// T1/T5). The participant is created via <see cref="Participant.Create"/> linked to the caller's own
    /// resolved user profile — never a client-supplied id, so the caller can only ever provision itself — with
    /// the token subject as the initial display name (a stable, always-present, control-character-free value;
    /// the participant may be renamed later), and persisted through the Participants-owned
    /// <see cref="IParticipantRepository.AddAsync"/>. The create is IDEMPOTENT on the filtered unique
    /// (workspace_id, user_id) index: a concurrent first read that won the create race makes this one a
    /// <see cref="ParticipantAddResult.DuplicateUserParticipant"/>, which re-resolves the existing participant so
    /// every caller returns the SAME stable participant id and no second participant is ever written.
    /// </summary>
    private static async Task<Participant?> SelfProvisionParticipantAsync(
        SelfEndpointDependencies deps,
        OidcPrincipal principal,
        TenantContext context,
        Guid workspaceId,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Object-level authorization is unchanged: only a member of the session's OWN workspace may self-provision.
        // The membership read leads with the organization id then the session's workspace id, so a control role
        // held in a DIFFERENT workspace never confers standing here (threat T5).
        var isMember = await deps.WorkspaceMembers
            .IsMemberAsync(context.OrganizationId, workspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (!isMember)
        {
            return null;
        }

        // Create the caller's participant, LINKED to its own resolved user profile (never a client-supplied id),
        // with the token subject as the initial display name. Active by the aggregate factory.
        var participant = Participant.Create(
            context.OrganizationId, workspaceId, context.UserProfileId, principal.SubjectId, timeProvider.GetUtcNow());

        var addResult = await deps.Participants.AddAsync(participant, cancellationToken).ConfigureAwait(false);
        if (addResult == ParticipantAddResult.DuplicateUserParticipant)
        {
            // A concurrent first read created the participant first; the filtered unique (workspace_id, user_id)
            // index rejected this second insert. Re-resolve the existing one so this caller returns the SAME stable
            // id (idempotent) instead of a second participant.
            return await deps.Participants
                .FindByUserAsync(context.OrganizationId, workspaceId, context.UserProfileId, cancellationToken)
                .ConfigureAwait(false);
        }

        return participant;
    }

    private static IResult ServiceUnavailable()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            code: ProblemCodes.ServiceUnavailable,
            title: "Service Unavailable",
            detail: "Resolving a session participant context requires persistence, which is not configured.");

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

    // Self-resolution is hidden: a malformed session id, a session in a foreign or non-entitled tenant, an
    // unknown session, a caller who is not a member of the session's workspace (so it cannot self-provision) and a
    // removed participant are ALL reported as 404, never distinguishable from each other and never echoing the
    // reason (docs/08; threats T1/T5).
    private static IResult HiddenParticipant()
        => CoreProblem.Create(
            statusCode: StatusCodes.Status404NotFound,
            code: ProblemCodes.NotFound,
            title: "Not Found",
            detail: "The requested resource was not found.");

    private readonly record struct SelfEndpointDependencies(
        TenantContextResolver Resolver,
        ISessionRepository Sessions,
        IWorkspaceMemberRepository WorkspaceMembers,
        IParticipantRepository Participants,
        RealtimeConnectionRegistry Connections);
}
