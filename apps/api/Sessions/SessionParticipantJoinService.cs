using LiveCore.Api.Participants;

namespace LiveCore.Api.Sessions;

/// <summary>
/// Session participant join service of the Sessions module (CORE-SES-003).
///
/// This is the single fail-closed decision of whether a participant may join a
/// session: it turns an (organization, workspace, session, participant) request
/// into either an admission (<see cref="SessionJoinResult"/> carrying the validated
/// ids) or a typed denial. It is the exact precedent of CORE-ID-005's
/// <c>TenantContextResolver</c> — a plain service over repositories that returns a
/// trusted result or a fail-closed denial — applied to the session-join boundary.
///
/// The Sessions module naturally depends on Participants (a session admits
/// participants), so this service consumes the <see cref="ISessionRepository"/> and
/// the <see cref="IParticipantRepository"/> through their explicit module contracts.
/// That is the allowed cross-module access (docs/02_ARCHITECTURE.md; docs/05_MODULE_CONTRACTS.md:
/// modules talk through contracts, not each other's tables), not direct table
/// access.
///
/// Join rule (defence in depth, threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md):
/// a participant may join a session only when ALL of the following hold, checked in
/// order and denying at the first failure, each denial hiding existence so nothing
/// outside the caller's tenant/workspace is ever leaked:
/// <list type="number">
///   <item>a session with the requested id exists WITHIN the requested organization
///   and workspace — a session in another organization or workspace is reported as
///   <see cref="SessionJoinDenialReason.SessionNotFound"/>, never leaked
///   (docs/06_AUTHORIZATION_MATRIX.md: the organization boundary is checked before
///   the workspace boundary; the repository lookup enforces both);</item>
///   <item>a participant with the requested id exists WITHIN the same organization
///   and workspace — a participant in another organization or workspace is reported
///   as <see cref="SessionJoinDenialReason.ParticipantNotFound"/>, never leaked;</item>
///   <item>the loaded session and participant both re-assert that they belong to
///   exactly the requested (organization, workspace, id) — a belt-and-suspenders
///   re-validation mirroring <c>TenantContext.Create</c>, so an admission can never
///   be minted from mismatched inputs;</item>
///   <item>the participant holds standing: its status is
///   <see cref="ParticipantStatus.Active"/>; a removed participant is denied with
///   <see cref="SessionJoinDenialReason.ParticipantNotActive"/> (CORE-SES-001);</item>
///   <item>the session is joinable: its status is
///   <see cref="SessionStatus.Prepared"/> or <see cref="SessionStatus.Live"/>; an
///   ended session is denied with
///   <see cref="SessionJoinDenialReason.SessionNotJoinable"/> (CORE-SES-002).</item>
/// </list>
///
/// This service persists nothing and emits nothing. It computes only the security
/// decision. The durable <c>ParticipantJoined</c> session event and its delivery
/// over SignalR (docs/09_EVENT_CATALOG.md; docs/11_REALTIME_SYNC.md) are the later
/// Realtime epic, and the persisted participant connection metadata is
/// Participants-owned later work; the join HTTP endpoint is a later story. Mapping a
/// denial to a 404/403 response and assigning the participant to a realtime group
/// are deliberately not done here.
/// </summary>
public sealed class SessionParticipantJoinService
{
    private readonly ISessionRepository _sessions;
    private readonly IParticipantRepository _participants;

    public SessionParticipantJoinService(
        ISessionRepository sessions,
        IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(participants);

        _sessions = sessions;
        _participants = participants;
    }

    /// <summary>
    /// Decides whether the participant identified by
    /// <paramref name="participantId"/> may join the session identified by
    /// <paramref name="sessionId"/>, both scoped to the given organization and
    /// workspace, and returns an admission carrying the validated ids or a typed
    /// fail-closed denial. The decision never crosses the tenant or workspace
    /// boundary: a session or participant outside the requested (organization,
    /// workspace) is hidden as not-found (threat T1/T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any of the organization, workspace, session or participant id is empty. An
    /// empty id can never address a real resource, so it is rejected rather than
    /// silently treated as not-found (consistent with the repository contracts,
    /// which reject empty ids).
    /// </exception>
    public async Task<SessionJoinResult> JoinAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a real resource. Reject them up front so the
        // decision is a clean ArgumentException, not an ambiguous not-found, and so
        // the fail-fast matches the repository contracts (which also reject empty
        // ids).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (participantId == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(participantId));
        }

        // The session must exist within exactly this organization and workspace.
        // The repository lookup is scoped by both boundaries (organization before
        // workspace), so a session under another tenant or workspace is never
        // returned; its absence is reported as not-found, hiding existence
        // (threat T1/T5).
        var session = await _sessions
            .FindByIdAsync(organizationId, workspaceId, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotFound);
        }

        // The participant must exist within the same organization and workspace.
        // Same scoping, same existence-hiding as the session lookup (threat T1/T5).
        var participant = await _participants
            .FindByIdAsync(organizationId, workspaceId, participantId, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotFound);
        }

        // Defence in depth (mirrors TenantContext.Create): re-assert that the loaded
        // session and participant each belong to exactly the requested
        // (organization, workspace, id). The repository already scoped the lookups,
        // so a mismatch indicates a programming error, but re-checking here means an
        // admission can never be minted from a session or participant that does not
        // identify the requested boundary. A mismatch hides as not-found rather than
        // leaking that a row was returned (threat T1/T5).
        if (!session.Identifies(organizationId, workspaceId, sessionId))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotFound);
        }

        if (!participant.Identifies(organizationId, workspaceId, participantId))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotFound);
        }

        // Lifecycle gate (participant): only an active participant holds standing to
        // join; a removed participant is denied (CORE-SES-001). The status is
        // compared by equality, never by ordering: ParticipantStatus is non-linear
        // and persisted by name.
        if (participant.Status != ParticipantStatus.Active)
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.ParticipantNotActive);
        }

        // Lifecycle gate (session): only a joinable session admits a participant. A
        // session is joinable while Prepared or Live; an ended session is denied
        // (CORE-SES-002). The joinability is expressed by an intention-revealing
        // predicate (IsJoinable) over the status, never by ordering: SessionStatus is
        // non-linear and persisted by name.
        if (!IsJoinable(session.Status))
        {
            return SessionJoinResult.Denied(SessionJoinDenialReason.SessionNotJoinable);
        }

        // Every check passed: admit the join. The result carries the validated,
        // boundary-checked ids only; nothing is persisted and no event is emitted
        // (the durable ParticipantJoined event and realtime group assignment are
        // later stories).
        return SessionJoinResult.Admit(organizationId, workspaceId, sessionId, participantId);
    }

    /// <summary>
    /// Whether a session in the given status may be joined by a participant: a
    /// session is joinable only while it is <see cref="SessionStatus.Prepared"/> or
    /// <see cref="SessionStatus.Live"/>. An <see cref="SessionStatus.Ended"/> session
    /// has a closed live timeline and admits no one. The check is an explicit
    /// allow-list of the two joinable states, never a "greater/less than" comparison:
    /// <see cref="SessionStatus"/> is non-linear and persisted by its name, so its
    /// integer values carry no ordering meaning (SessionStatus xmldoc).
    /// </summary>
    private static bool IsJoinable(SessionStatus status)
        => status is SessionStatus.Prepared or SessionStatus.Live;
}
