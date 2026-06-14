using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.Sessions;

/// <summary>
/// Session participant LEAVE flow of the Sessions module (CORE-EVT-002) — the symmetric counterpart of the
/// <see cref="SessionParticipantJoinService"/>'s join. It removes a participant from a session's audience and,
/// when (and only when) a participant actually leaves, emits the durable <c>ParticipantLeft</c> session event
/// (docs/09_EVENT_CATALOG.md: "ParticipantLeft | System | Host/CoHost | yes | participant feed optional").
///
/// The leave is performed over the participant aggregate's own soft-delete, <see cref="Participant.Remove"/>
/// (the Participants module owns the participant record, docs/05_MODULE_CONTRACTS.md): the participant's
/// status moves to <see cref="ParticipantStatus.Removed"/> and is persisted, then the event is published. The
/// event is published through the reused <see cref="ISessionEventPublisher"/> (the same seam the join, reveal
/// and start/end commands use), so the Realtime module stays the sole owner of delivery and the anti-leak
/// recipient resolver is not duplicated. It is a SUBJECTLESS audience event carrying the participant
/// IDENTIFIER only — never a display name or any other PII (threat T7; see
/// <see cref="ParticipantPresenceEvent"/>) — and a System-emitted event (no actor), so it reaches the session
/// hosts (always — host-visible), the observers and the remaining active participants. Because the participant
/// is removed BEFORE the event is published, the just-departed participant is no longer in the
/// active-participant fan-out, so a leaver never receives their own removal (the optional participant feed).
///
/// Fail-closed and tenant-isolated, exactly like the join service (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md): the session and the participant are each loaded through their
/// (organization, workspace)-scoped repository lookups (the organization boundary checked before the
/// workspace boundary, docs/06_AUTHORIZATION_MATRIX.md), so a session or participant outside the requested
/// tenant/workspace is hidden as not-found and NO event is emitted. Removing a participant that has already
/// left is an idempotent no-op that emits nothing, so each real departure appends EXACTLY ONE event while a
/// repeat appends none.
///
/// On (and only on) an actual departure it also RE-AUTHORIZES the realtime layer (CORE-RTC-002): once the
/// participant is removed it raises the <see cref="IRealtimeConnectionEvictor"/> seam to abort any still-open
/// hub connection the participant holds in this session, so their existing socket stops receiving events the
/// instant they leave rather than keeping its group membership until it reconnects (threat T3). The eviction
/// reuses the Realtime evictor — it never widens an audience and never duplicates the authorization decision —
/// and is best-effort, like the delivery, so the durable <c>ParticipantLeft</c> is the source of truth even if
/// the transport eviction is a no-op (the connection was already gone, or is held by another instance).
///
/// This service decides only the leave transition, its event and the connection eviction. The leave HTTP
/// endpoint (mapping a not-found to a 404 and authorizing the caller's role) is a later story, exactly as the
/// join HTTP endpoint is.
/// </summary>
public sealed class SessionParticipantLeaveService
{
    private readonly ISessionRepository _sessions;
    private readonly IParticipantRepository _participants;
    private readonly ISessionEventPublisher _eventPublisher;
    private readonly IRealtimeConnectionEvictor _connectionEvictor;
    private readonly TimeProvider _timeProvider;

    public SessionParticipantLeaveService(
        ISessionRepository sessions,
        IParticipantRepository participants,
        ISessionEventPublisher eventPublisher,
        IRealtimeConnectionEvictor connectionEvictor,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(connectionEvictor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _sessions = sessions;
        _participants = participants;
        _eventPublisher = eventPublisher;
        _connectionEvictor = connectionEvictor;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Removes the participant identified by <paramref name="participantId"/> from the session identified by
    /// <paramref name="sessionId"/>, both scoped to the given organization and workspace, and returns the
    /// outcome. On (and only on) an actual departure — the participant was active and is soft-removed — it
    /// appends and delivers the durable <c>ParticipantLeft</c> session event (CORE-EVT-002) before returning.
    /// A participant that had already left is an idempotent no-op that emits nothing; a session or participant
    /// outside the requested (organization, workspace) is hidden as not-found and emits nothing (threat
    /// T1/T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Any of the organization, workspace, session or participant id is empty. An empty id can never address a
    /// real resource, so it is rejected rather than silently treated as not-found (consistent with the
    /// repository contracts, which reject empty ids).
    /// </exception>
    public async Task<SessionParticipantLeaveResult> LeaveAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a real resource. Reject them up front so the outcome is a clean
        // ArgumentException, not an ambiguous not-found, matching the join service and the repository
        // contracts.
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

        // The session must exist within exactly this organization and workspace (the event is session-scoped,
        // so it is anchored to a real session in the tenant). The repository lookup is scoped by both
        // boundaries (organization before workspace), so a session under another tenant or workspace is never
        // returned; its absence is hidden as not-found (threat T1/T5). The session check runs first, mirroring
        // the join service, so session existence is hidden before any participant state is consulted.
        var session = await _sessions
            .FindByIdAsync(organizationId, workspaceId, sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null || !session.Identifies(organizationId, workspaceId, sessionId))
        {
            return SessionParticipantLeaveResult.NotFound(ParticipantLeaveOutcome.SessionNotFound);
        }

        // The participant must exist within the same organization and workspace. Same scoping, same
        // existence-hiding (threat T1/T5).
        var participant = await _participants
            .FindByIdAsync(organizationId, workspaceId, participantId, cancellationToken)
            .ConfigureAwait(false);
        if (participant is null || !participant.Identifies(organizationId, workspaceId, participantId))
        {
            return SessionParticipantLeaveResult.NotFound(ParticipantLeaveOutcome.ParticipantNotFound);
        }

        // A participant that has already left holds no presence to remove: the leave is idempotent, so this is
        // a no-op that changes nothing and emits no event (so a repeat never appends a second ParticipantLeft).
        // The status is compared by equality, never by ordering: ParticipantStatus is non-linear and persisted
        // by name.
        if (participant.Status == ParticipantStatus.Removed)
        {
            return SessionParticipantLeaveResult.AlreadyLeft(organizationId, workspaceId, sessionId, participantId);
        }

        // The participant actually leaves: soft-remove it (the Participants-owned transition) and persist the
        // new status FIRST, so the participant is no longer active when the event fans out — a leaver never
        // receives their own removal.
        var now = _timeProvider.GetUtcNow();
        participant.Remove(now);
        await _participants.UpdateAsync(participant, cancellationToken).ConfigureAwait(false);

        // Emit the durable ParticipantLeft session event (CORE-EVT-002). It is a subjectless audience event
        // carrying the participant IDENTIFIER only — never a display name or any PII (threat T7) — and a
        // System-emitted event (no actor), delivered through the reused publisher + recipient resolver to the
        // hosts (always — host-visible) and the remaining audience (docs/09_EVENT_CATALOG.md: "ParticipantLeft
        // | System").
        var sessionEvent = ParticipantPresenceEvent.Create(
            SessionEventTypes.ParticipantLeft,
            organizationId,
            workspaceId,
            sessionId,
            participantId,
            createdBy: null,
            now);
        await _eventPublisher.PublishAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // Re-authorize the realtime layer (CORE-RTC-002): the participant no longer has standing, so abort any
        // still-open hub connection they hold in this session. Without this their existing socket would stay in
        // its participant group and keep receiving any later targeted delivery until it reconnected; eviction
        // stops it now, not only on reconnect (threat T3). It only ever removes a connection — never widens an
        // audience — and reuses the Realtime evictor seam, not a parallel authorization. Best-effort, like the
        // delivery above: the durable ParticipantLeft is already recorded, so a transport hiccup here changes
        // no persisted state.
        await _connectionEvictor
            .EvictParticipantAsync(organizationId, workspaceId, sessionId, participantId, cancellationToken)
            .ConfigureAwait(false);

        return SessionParticipantLeaveResult.Departed(organizationId, workspaceId, sessionId, participantId);
    }
}
