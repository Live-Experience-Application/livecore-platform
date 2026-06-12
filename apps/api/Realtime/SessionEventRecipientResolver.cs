using LiveCore.Api.Participants;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Computes the per-recipient deliveries of a session event (CORE-RT-004,
/// <see cref="ISessionEventRecipientResolver"/>) — the "recipient-specific event projection" that
/// realizes the documented "compute recipients -> project payload" steps (docs/11_REALTIME_SYNC.md) and
/// the threat model's required control "per-recipient event projection" (threat T3 in
/// docs/07_SECURITY_THREAT_MODEL.md: "Realtime delivery never leaks hidden events"). It supersedes the
/// CORE-RT-003 coarse group routing, which delivered the SAME envelope to whole groups and never fanned
/// an audience event out to participants.
///
/// THE DECISIONS (every group name comes from <see cref="RealtimeGroups"/>, never client input):
/// <list type="bullet">
///   <item>The SESSION HOSTS group always receives the event — host-content roles may see everything
///   (docs/06_AUTHORIZATION_MATRIX.md) — with the HOST projection (<see cref="SessionEventEnvelope.ForHost"/>),
///   which carries the "to whom" routing confirmation (docs/09_EVENT_CATALOG.md "host receives
///   audit/confirmation event").</item>
///   <item>A SELECTED-participant event (<see cref="SessionEvent.TargetParticipantId"/> set) is delivered
///   to ONLY that one participant's group (plus hosts) — never to observers or any other participant — and
///   only when they may see the subject. A non-selected participant is not in that group AND would fail
///   the per-participant visibility gate, so a private event can never leak to them (THE crown jewel,
///   threat T3).</item>
///   <item>An AUDIENCE-WIDE event is delivered to the OBSERVERS group when the audience may see the
///   subject, and is FANNED OUT to EACH active participant of the session's workspace whose own
///   per-participant visibility allows it (docs/11_REALTIME_SYNC.md has no all-participants group, so the
///   audience reaches participants only through their individual groups). The audience projection
///   (<see cref="SessionEventEnvelope.ForAudience"/>) omits the routing target, so a participant never
///   learns who else was targeted (threats T2/T7).</item>
/// </list>
///
/// THE VISIBILITY GATE is the central Visibility engine, NOT a parallel copy: every per-recipient and
/// audience decision is delegated to <see cref="IEventRecipientVisibility"/> (which reuses
/// <see cref="VisibilityPolicy"/>), so the realtime recipient set can never diverge from the REST
/// visibility decision (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic elsewhere"). An
/// event with NO visibility subject (<see cref="SessionEvent.HasVisibilitySubject"/> false — an
/// unconditional audience event such as a later <c>SessionStarted</c>) is not gated: the audience and all
/// active participants receive it. The active-participant set is the session's audience: participants are
/// workspace-scoped (CORE-SES-001), so they are the workspace's ACTIVE participants
/// (<see cref="IParticipantRepository.ListActiveByWorkspaceAsync"/>), the same population a participant
/// realtime connection is admitted from (CORE-RT-002).
/// </summary>
internal sealed class SessionEventRecipientResolver : ISessionEventRecipientResolver
{
    private readonly IEventRecipientVisibility _visibility;
    private readonly IParticipantRepository _participants;

    public SessionEventRecipientResolver(
        IEventRecipientVisibility visibility,
        IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        ArgumentNullException.ThrowIfNull(participants);
        _visibility = visibility;
        _participants = participants;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEventDelivery>> ResolveAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        var deliveries = new List<SessionEventDelivery>();

        // Hosts always receive the event (host-content roles see everything), with the host projection
        // that carries the routing confirmation.
        deliveries.Add(new SessionEventDelivery(
            RealtimeGroups.SessionHosts(sessionEvent.SessionId),
            SessionEventEnvelope.ForHost(sessionEvent)));

        var audienceEnvelope = SessionEventEnvelope.ForAudience(sessionEvent);

        if (sessionEvent.TargetParticipantId is { } selectedParticipantId)
        {
            // SELECTED / private: only the selected participant, and only if they may see the subject.
            // No observers, no other participants.
            if (await CanParticipantReceiveAsync(sessionEvent, selectedParticipantId, cancellationToken)
                .ConfigureAwait(false))
            {
                deliveries.Add(new SessionEventDelivery(
                    RealtimeGroups.SessionParticipant(sessionEvent.SessionId, selectedParticipantId),
                    audienceEnvelope));
            }

            return deliveries;
        }

        // AUDIENCE-WIDE: observers (gated), then a per-participant fan-out (each gated).
        if (await CanAudienceReceiveAsync(sessionEvent, cancellationToken).ConfigureAwait(false))
        {
            deliveries.Add(new SessionEventDelivery(
                RealtimeGroups.SessionObservers(sessionEvent.SessionId),
                audienceEnvelope));
        }

        var participants = await _participants
            .ListActiveByWorkspaceAsync(sessionEvent.OrganizationId, sessionEvent.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);

        foreach (var participant in participants)
        {
            if (await CanParticipantReceiveAsync(sessionEvent, participant.Id, cancellationToken)
                .ConfigureAwait(false))
            {
                deliveries.Add(new SessionEventDelivery(
                    RealtimeGroups.SessionParticipant(sessionEvent.SessionId, participant.Id),
                    audienceEnvelope));
            }
        }

        return deliveries;
    }

    /// <summary>
    /// Whether the audience may receive the event: an event with no visibility subject is unconditional;
    /// otherwise the central Visibility engine decides (audience viewpoint).
    /// </summary>
    private async Task<bool> CanAudienceReceiveAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        => !sessionEvent.HasVisibilitySubject
            || await _visibility
                .CanAudienceReceiveAsync(
                    sessionEvent.OrganizationId,
                    sessionEvent.WorkspaceId,
                    sessionEvent.VisibilitySubjectType!,
                    sessionEvent.VisibilitySubjectId!.Value,
                    cancellationToken)
                .ConfigureAwait(false);

    /// <summary>
    /// Whether the given participant may receive the event: an event with no visibility subject is
    /// unconditional; otherwise the central Visibility engine decides (per-participant).
    /// </summary>
    private async Task<bool> CanParticipantReceiveAsync(
        SessionEvent sessionEvent,
        Guid participantId,
        CancellationToken cancellationToken)
        => !sessionEvent.HasVisibilitySubject
            || await _visibility
                .CanParticipantReceiveAsync(
                    sessionEvent.OrganizationId,
                    sessionEvent.WorkspaceId,
                    participantId,
                    sessionEvent.VisibilitySubjectType!,
                    sessionEvent.VisibilitySubjectId!.Value,
                    cancellationToken)
                .ConfigureAwait(false);
}
