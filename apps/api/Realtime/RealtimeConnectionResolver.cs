using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Resolves which SERVER-MANAGED groups a realtime hub connection joins (CORE-RT-002), or a fail-closed
/// denial. This is the heart of "server-managed session groups": the connection supplies only
/// IDENTIFIERS (the organization slug, the session id and, for a participant, their participant id); the
/// server resolves the caller's authorized relationship to the session and COMPUTES the group names
/// (<see cref="RealtimeGroups"/>), so a client never chooses a group and can never subscribe to another
/// participant's feed (docs/11_REALTIME_SYNC.md; threat T3 in docs/07_SECURITY_THREAT_MODEL.md). It is a
/// plain decision service over the existing tenant/session/membership/participant building blocks —
/// mirroring <c>SessionParticipantJoinService</c> (CORE-SES-003) and superseding the RT-001
/// authenticated-connection placeholder, which only decided WHETHER a connection was authenticated.
///
/// Resolution order (deny at the first failure, never leaking which step failed — threats T1/T5):
/// <list type="number">
///   <item>The connection must carry an authenticated, mappable OIDC principal (fail-closed auth, the
///   RT-001 gate).</item>
///   <item>The caller must be entitled to the target organization — the same claim-AND-membership tenant
///   check the REST endpoints use (<see cref="TenantContextResolver"/>; threat T5).</item>
///   <item>The session must exist within that tenant; its own workspace is taken from the loaded row
///   (the organization boundary leads, exactly like the reveal/start-end commands).</item>
///   <item>The caller's relationship to the session's workspace is then classified and mapped to the
///   minimal groups for that relationship (below).</item>
/// </list>
///
/// Relationship → groups (each connection joins ONLY the groups its relationship needs — minimal
/// exposure, so a later event delivered to a group can never reach an unrelated connection, threat T3):
/// <list type="bullet">
///   <item>A PARTICIPANT — the caller owns an ACTIVE participant record (the supplied participant id) in
///   the session's own workspace — joins ONLY <c>session:{id}:participant:{participantId}</c>. The
///   participant must be linked to the caller's user (an anonymous participant has no owner, so anonymous
///   realtime is deferred) and be in the session's workspace; otherwise denied (mirrors the visible-feed
///   own-feed rule).</item>
///   <item>A HOST-capable workspace member (Owner/Admin/Host/CoHost, via
///   <see cref="VisibilityRoles.ViewsHostOnlyContent"/> — reused, not re-listed) joins the org, the
///   workspace-hosts and the session-hosts groups.</item>
///   <item>An OBSERVER workspace member joins ONLY <c>session:{id}:observers</c>.</item>
///   <item>Anything else — a Participant/Auditor workspace role with no participant record, a non-member,
///   a foreign tenant/workspace — has no defined realtime group and is DENIED (fail-closed). Anonymous
///   participants and an auditor realtime channel are deferred (the group taxonomy defines neither).</item>
/// </list>
/// A participant id is honored only on the participant path; a host/observer connection supplies none.
/// </summary>
internal sealed class RealtimeConnectionResolver
{
    private readonly TenantContextResolver _tenantContextResolver;
    private readonly ISessionRepository _sessions;
    private readonly IWorkspaceMemberRepository _workspaceMembers;
    private readonly IParticipantRepository _participants;

    public RealtimeConnectionResolver(
        TenantContextResolver tenantContextResolver,
        ISessionRepository sessions,
        IWorkspaceMemberRepository workspaceMembers,
        IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(tenantContextResolver);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(workspaceMembers);
        ArgumentNullException.ThrowIfNull(participants);

        _tenantContextResolver = tenantContextResolver;
        _sessions = sessions;
        _workspaceMembers = workspaceMembers;
        _participants = participants;
    }

    /// <summary>
    /// Resolves the groups the given connection joins, or a fail-closed denial. The inputs are the
    /// connection's authenticated principal and the identifiers it supplied (the organization slug, the
    /// session id, and an optional participant id for a participant connection).
    /// </summary>
    public async Task<RealtimeConnectionAdmission> ResolveAsync(
        ClaimsPrincipal? user,
        string? organizationSlug,
        Guid sessionId,
        Guid? participantId,
        CancellationToken cancellationToken)
    {
        // 1. Fail-closed authentication: the connection must carry a mappable OIDC principal.
        if (user is null)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.InvalidPrincipal);
        }

        var mapping = OidcPrincipalMapper.Map(user);
        if (!mapping.Succeeded)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.InvalidPrincipal);
        }

        // A blank slug or an empty session id can never address a tenant/session; deny without touching
        // the database (an empty session id would also be rejected by the scoped repository lookup).
        if (string.IsNullOrWhiteSpace(organizationSlug))
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.TenantDenied);
        }

        if (sessionId == Guid.Empty)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.SessionNotFound);
        }

        // An explicitly empty (non-null) participant id can never address a participant.
        if (participantId == Guid.Empty)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.NotAuthorizedForSession);
        }

        // 2. Tenant boundary: claim AND membership (the organization boundary is checked first).
        var resolution = await _tenantContextResolver
            .ResolveAsync(mapping.Principal, organizationSlug, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.TenantDenied);
        }

        var context = resolution.Context;

        // 3. Load the session within the resolved tenant; a cross-tenant or unknown session is hidden.
        var session = await _sessions
            .FindByIdInOrganizationAsync(context.OrganizationId, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.SessionNotFound);
        }

        // 4. Classify the caller's relationship to the session and compute the groups.
        return participantId is { } pid
            ? await ResolveParticipantAsync(context, session, pid, cancellationToken).ConfigureAwait(false)
            : await ResolveMemberAsync(context, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admits a PARTICIPANT connection: the caller must own an ACTIVE participant (the supplied id) in
    /// the session's own workspace. Joins only that participant's group.
    /// </summary>
    private async Task<RealtimeConnectionAdmission> ResolveParticipantAsync(
        TenantContext context,
        Session session,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        var participant = await _participants
            .FindByIdInOrganizationAsync(context.OrganizationId, participantId, cancellationToken)
            .ConfigureAwait(false);

        // The participant must exist in the SESSION'S own workspace, be OWNED by the caller (an anonymous
        // participant is owned by no one), and be ACTIVE. Any miss is hidden as the same denial so a host
        // cannot probe for a participant outside their session (threat T5).
        if (participant is null
            || participant.WorkspaceId != session.WorkspaceId
            || !participant.BelongsToSubject(context.UserProfileId)
            || participant.Status != ParticipantStatus.Active)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.NotAuthorizedForSession);
        }

        return RealtimeConnectionAdmission.Admit(
        [
            RealtimeGroups.SessionParticipant(session.Id, participant.Id),
        ]);
    }

    /// <summary>
    /// Admits a HOST or OBSERVER connection by the caller's role in the session's own workspace. A
    /// host-capable role joins the org/workspace-hosts/session-hosts groups; an Observer joins the
    /// session-observers group; any other role (including a Participant/Auditor workspace role with no
    /// participant record) is denied.
    /// </summary>
    private async Task<RealtimeConnectionAdmission> ResolveMemberAsync(
        TenantContext context,
        Session session,
        CancellationToken cancellationToken)
    {
        var member = await _workspaceMembers
            .FindAsync(context.OrganizationId, session.WorkspaceId, context.UserProfileId, cancellationToken)
            .ConfigureAwait(false);
        if (member is null)
        {
            return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.NotAuthorizedForSession);
        }

        // Host-capable roles (reusing the Visibility module's canonical host-content set, not a
        // re-listed copy) join the org + workspace-hosts + session-hosts groups.
        if (VisibilityRoles.ViewsHostOnlyContent(member.Role))
        {
            return RealtimeConnectionAdmission.Admit(
            [
                RealtimeGroups.Organization(context.OrganizationId),
                RealtimeGroups.WorkspaceHosts(session.WorkspaceId),
                RealtimeGroups.SessionHosts(session.Id),
            ]);
        }

        // The Observer role joins only the session-observers group.
        if (member.HasRole(MembershipRole.Observer))
        {
            return RealtimeConnectionAdmission.Admit(
            [
                RealtimeGroups.SessionObservers(session.Id),
            ]);
        }

        // A Participant/Auditor workspace role (no participant record on this connection) or any other
        // value has no defined realtime group: deny, fail-closed.
        return RealtimeConnectionAdmission.Deny(RealtimeConnectionDenialReason.NotAuthorizedForSession);
    }
}
