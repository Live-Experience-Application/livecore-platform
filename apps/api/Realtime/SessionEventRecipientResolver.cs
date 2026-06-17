// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
///   <item>An AUDIENCE-WIDE event is delivered to the OBSERVERS group AND the shared SESSION-AUDIENCE group
///   (<see cref="RealtimeGroups.SessionAudience"/>, CORE-PERF-001) when the audience may see the subject —
///   ONE backplane publish each, reaching every active participant through the shared group instead of one
///   publish per participant. The audience projection (<see cref="SessionEventEnvelope.ForAudience"/>)
///   omits the routing target, so a participant never learns who else was targeted (threats T2/T7). When
///   the audience-at-large may NOT see the subject but a participant-SCOPED visible rule exists (a
///   selected-participant reveal on the same resource), that one event still reaches exactly those
///   participants through their OWN groups — derived in memory from the SAME single lookup, never one
///   query per participant.</item>
/// </list>
///
/// THE FAN-OUT IS COLLAPSED TO A SINGLE LOOKUP + SHARED GROUP (CORE-PERF-001). An audience-wide event used
/// to enumerate the workspace's active participants and run one visibility query (and one backplane
/// publish) PER participant — 1+N queries and N publishes per event, growing linearly with audience size.
/// Now the audience decision is resolved from ONE session-scoped rule lookup
/// (<see cref="IEventRecipientVisibility.ResolveAudienceRecipientsAsync"/>), and a visible audience event
/// is delivered to the shared session-audience group with ONE publish; only a SELECTED-participant event
/// uses a per-participant lookup/group. So per-reveal DB and backplane load no longer grow with audience
/// size, and visibility correctness is unchanged: the shared group is gated by the same audience-wide
/// decision and only ever carries events the whole audience is entitled to, while the participant-scoped
/// visible set reaches exactly the individually-entitled participants (the same recipient set the old
/// fan-out produced).
///
/// THE RECIPIENT SET IS BOUNDED BY THE EVENT'S SESSION (CORE-SVIS-001). EVERY group this resolver emits is
/// keyed by <see cref="SessionEvent.SessionId"/> (<see cref="RealtimeGroups.SessionHosts"/> /
/// <see cref="RealtimeGroups.SessionObservers"/> / <see cref="RealtimeGroups.SessionAudience"/> /
/// <see cref="RealtimeGroups.SessionParticipant"/>), and every visibility gate below is the SESSION-SCOPED
/// decision (it passes the event's session, so only the reveal rules of THAT session are consulted). So a
/// reveal in one session can never be delivered into a concurrent session of the same workspace: a
/// connection joins only its own session's groups (<see cref="RealtimeConnectionResolver"/>), so the shared
/// audience group of one session reaches only the participants connected to THAT session, and the
/// session-scoped gate independently confirms the subject is revealed in this session (the cross-session
/// leak; threat T5/T3).
///
/// THE VISIBILITY GATE is the central Visibility engine, NOT a parallel copy: every per-recipient and
/// audience decision is delegated to <see cref="IEventRecipientVisibility"/> (which reuses
/// <see cref="VisibilityPolicy"/>, now session-scoped), so the realtime recipient set can never diverge
/// from the REST visibility decision (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic
/// elsewhere"). An event with NO visibility subject (<see cref="SessionEvent.HasVisibilitySubject"/>
/// false — an unconditional audience event such as a later <c>SessionStarted</c>) is not gated: the
/// observers and the whole session audience (the shared group) receive it. The session's audience is its
/// workspace's active participants — there is no persisted session-participant roster yet (the participant
/// connection metadata is deferred — CORE-PRS-001 and the
/// <see cref="LiveCore.Api.Sessions.SessionParticipantJoinService"/> note) — and each such participant
/// joins the session-keyed shared audience group on connect (CORE-RT-002), so a delivery to that group
/// reaches exactly the session's connected audience. A departed participant is taken out of the audience by
/// the eviction seam (CORE-RTC-002, immediate on the instance holding the socket); unlike the old fan-out,
/// audience delivery is no longer additionally re-gated per event by active status, so cross-instance
/// eviction (a documented follow-up, docs/11_REALTIME_SYNC.md) now also covers the audience group.
/// </summary>
internal sealed class SessionEventRecipientResolver : ISessionEventRecipientResolver
{
    private readonly IEventRecipientVisibility _visibility;

    public SessionEventRecipientResolver(IEventRecipientVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        _visibility = visibility;
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
            // No observers, no shared audience group, no other participants. Per-participant lookups and
            // groups are reserved for selected-participant events (CORE-PERF-001).
            if (await CanParticipantReceiveAsync(sessionEvent, selectedParticipantId, cancellationToken)
                .ConfigureAwait(false))
            {
                deliveries.Add(new SessionEventDelivery(
                    RealtimeGroups.SessionParticipant(sessionEvent.SessionId, selectedParticipantId),
                    audienceEnvelope));
            }

            return deliveries;
        }

        // AUDIENCE-WIDE. An event with no visibility subject is unconditional: the observers and the whole
        // session audience receive it through the shared group — one publish each, no per-participant
        // fan-out (CORE-PERF-001).
        if (!sessionEvent.HasVisibilitySubject)
        {
            deliveries.Add(new SessionEventDelivery(
                RealtimeGroups.SessionObservers(sessionEvent.SessionId), audienceEnvelope));
            deliveries.Add(new SessionEventDelivery(
                RealtimeGroups.SessionAudience(sessionEvent.SessionId), audienceEnvelope));
            return deliveries;
        }

        // Subject-gated audience event: ONE session-scoped rule lookup yields both the audience-wide
        // decision and the participant-scoped visible set (CORE-PERF-001), so the recipient set is computed
        // without a per-participant query.
        var audience = await _visibility
            .ResolveAudienceRecipientsAsync(
                sessionEvent.OrganizationId,
                sessionEvent.WorkspaceId,
                sessionEvent.SessionId,
                sessionEvent.VisibilitySubjectType!,
                sessionEvent.VisibilitySubjectId!.Value,
                cancellationToken)
            .ConfigureAwait(false);

        if (audience.AudienceVisible)
        {
            // The whole audience may see it: observers + the shared session-audience group (one publish
            // each). The shared group covers EVERY connected participant — including any with a
            // participant-scoped rule — so no per-participant delivery is needed.
            deliveries.Add(new SessionEventDelivery(
                RealtimeGroups.SessionObservers(sessionEvent.SessionId), audienceEnvelope));
            deliveries.Add(new SessionEventDelivery(
                RealtimeGroups.SessionAudience(sessionEvent.SessionId), audienceEnvelope));
        }
        else
        {
            // The audience-at-large may NOT see it, but a participant-scoped visible rule still reaches
            // exactly that participant (a selected-participant reveal on the same resource). Deliver to each
            // such participant's OWN group — derived from the SAME single lookup, never a per-participant
            // query — and never to the shared audience group or the observers (threat T3).
            foreach (var participantId in audience.SelectedVisibleParticipantIds)
            {
                deliveries.Add(new SessionEventDelivery(
                    RealtimeGroups.SessionParticipant(sessionEvent.SessionId, participantId),
                    audienceEnvelope));
            }
        }

        return deliveries;
    }

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
