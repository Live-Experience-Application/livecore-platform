// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
///   <item>A HOST-ONLY event (<see cref="SessionEventTypes.IsHostOnly"/> — the catalog's preparation/output
///   events <c>SessionCreated</c> and <c>RecapGenerated</c>, CORE-EVT-004) reaches the hosts group and STOPS:
///   no observers and no participants, live or on reconnect replay. It is a subject-INDEPENDENT host-facing
///   routing class (the catalog marks it visible to hosts only), so the audience never receives it even after
///   a resource it concerns is later revealed (threats T2/T7).</item>
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
/// THE RECIPIENT SET IS BOUNDED BY THE EVENT'S SESSION (CORE-SVIS-001). EVERY group this resolver emits is
/// keyed by <see cref="SessionEvent.SessionId"/> (<see cref="RealtimeGroups.SessionHosts"/> /
/// <see cref="RealtimeGroups.SessionObservers"/> / <see cref="RealtimeGroups.SessionParticipant"/>), and
/// every visibility gate below is the SESSION-SCOPED decision (it passes the event's session, so only the
/// reveal rules of THAT session are consulted). So a reveal in one session can never be delivered into a
/// concurrent session of the same workspace: a connection joins only its own session's groups
/// (<see cref="RealtimeConnectionResolver"/>), so even though the audience fan-out ENUMERATES the
/// workspace's active participants (the audience candidate set — see below), a delivery addressed to
/// <c>session:{thisSession}:participant:{p}</c> reaches a participant only when they are connected to
/// THIS session, and the session-scoped gate independently confirms the subject is revealed in this
/// session (the cross-session leak; threat T5/T3).
///
/// THE VISIBILITY GATE is the central Visibility engine, NOT a parallel copy: every per-recipient and
/// audience decision is delegated to <see cref="IEventRecipientVisibility"/> (which reuses
/// <see cref="VisibilityPolicy"/>, now session-scoped), so the realtime recipient set can never diverge
/// from the REST visibility decision (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic
/// elsewhere"). An event with NO visibility subject (<see cref="SessionEvent.HasVisibilitySubject"/>
/// false — an unconditional audience event such as a later <c>SessionStarted</c>) is not gated: the
/// audience and all active participants of the session receive it. The active-participant CANDIDATE set is
/// the session's audience: participants are workspace-scoped (CORE-SES-001) and there is no persisted
/// session-participant roster yet (the participant connection metadata is deferred — CORE-PRS-001 and the
/// <see cref="LiveCore.Api.Sessions.SessionParticipantJoinService"/> note), so the candidates are the
/// workspace's ACTIVE participants (<see cref="IParticipantRepository.ListActiveByWorkspaceAsync"/>), the
/// same population a participant realtime connection is admitted from (CORE-RT-002), each gated by the
/// session-scoped visibility above and addressed to its own session-keyed group.
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

        // HOST-ONLY events (CORE-EVT-004): the preparation/output events the catalog routes to the hosts
        // only — SessionCreated, RecapGenerated (SessionEventTypes.IsHostOnly). They reach the session hosts
        // group and STOP: no observers, no participants, live or on reconnect replay (the replay filter
        // re-runs this resolver, so it keeps them host-only). This NARROWS delivery — it can never widen an
        // audience — and is a subject-INDEPENDENT routing class: unlike a SceneActivated whose audience
        // tracks the scene's current visibility, a created session or a generated recap is host-facing
        // regardless of any later reveal, so a participant who joins and replays the stream never receives
        // the prep/output event (the catalog's "visible to Host/CoHost/Admin"; threats T2/T7).
        if (SessionEventTypes.IsHostOnly(sessionEvent.EventType))
        {
            return deliveries;
        }

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

        // The audience CANDIDATE set: the workspace's active participants (no persisted session-participant
        // roster exists yet — see the type summary). Each candidate is gated by the SESSION-SCOPED
        // visibility below and addressed to a group keyed by THIS event's session, so the recipient set is
        // bounded to the event's session even though the candidate source is workspace-wide (CORE-SVIS-001).
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
                    sessionEvent.SessionId,
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
                    sessionEvent.SessionId,
                    participantId,
                    sessionEvent.VisibilitySubjectType!,
                    sessionEvent.VisibilitySubjectId!.Value,
                    cancellationToken)
                .ConfigureAwait(false);
}
