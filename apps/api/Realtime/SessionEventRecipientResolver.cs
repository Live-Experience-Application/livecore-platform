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
/// BATCH RESOLUTION FOR RECONNECT REPLAY (CORE-PERF-002). <see cref="ResolveBatchAsync"/> resolves a whole
/// slice of events at once: it collects the slice's DISTINCT subjects and resolves their audience
/// visibility with ONE batched lookup (<see cref="IEventRecipientVisibility.ResolveAudienceRecipientsBatchAsync"/>)
/// instead of one query per event, then builds each event's deliveries from the SAME routing
/// (<c>BuildDeliveries</c>) the single <see cref="ResolveAsync"/> uses. So reconnect replay cost no longer
/// grows with the number of events, and a batched delivery list is byte-for-byte what the per-event path
/// would have produced — replay can never leak an event live delivery would have withheld (threat T3).
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

        // Resolve the audience visibility for THIS event (one session-scoped rule lookup when it needs one),
        // then build the deliveries from it. The routing lives in ONE place (BuildDeliveries), shared with
        // the batch path, so live delivery and reconnect replay can never diverge.
        var audience = await ResolveAudienceForEventAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        return BuildDeliveries(sessionEvent, audience);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyList<SessionEventDelivery>>> ResolveBatchAsync(
        IReadOnlyList<SessionEvent> sessionEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvents);

        if (sessionEvents.Count == 0)
        {
            return [];
        }

        // Resolve the audience visibility of every DISTINCT subject in the batch with a SINGLE batched lookup
        // per (tenant, workspace, session) — a replay slice is one session, so this is ONE lookup — instead
        // of one query per event (CORE-PERF-002). The map is then reused for every event that shares a
        // subject, so the query count does not grow with the number of replayed events.
        var resolved = await ResolveAudienceBatchAsync(sessionEvents, cancellationToken).ConfigureAwait(false);

        var deliveries = new List<IReadOnlyList<SessionEventDelivery>>(sessionEvents.Count);
        foreach (var sessionEvent in sessionEvents)
        {
            // Pull this event's pre-resolved audience visibility (None when it needs no audience decision —
            // a host-only or no-subject event — or, fail-closed, when its subject was unrecognized) and build
            // its deliveries from the SAME routing the single path uses.
            var audience = AudienceVisibility.None;
            if (NeedsAudienceDecision(sessionEvent)
                && resolved.TryGetValue(SubjectKey(sessionEvent), out var resolvedAudience))
            {
                audience = resolvedAudience;
            }

            deliveries.Add(BuildDeliveries(sessionEvent, audience));
        }

        return deliveries;
    }

    /// <summary>
    /// Resolves the audience visibility for a single event: <see cref="AudienceVisibility.None"/> when the
    /// event needs no audience decision (a host-only event, or one with no visibility subject — neither
    /// triggers a rule lookup), otherwise the central Visibility engine's SESSION-SCOPED single-subject
    /// resolution (CORE-PERF-001).
    /// </summary>
    private async Task<AudienceVisibility> ResolveAudienceForEventAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        if (!NeedsAudienceDecision(sessionEvent))
        {
            return AudienceVisibility.None;
        }

        return await _visibility
            .ResolveAudienceRecipientsAsync(
                sessionEvent.OrganizationId,
                sessionEvent.WorkspaceId,
                sessionEvent.SessionId,
                sessionEvent.VisibilitySubjectType!,
                sessionEvent.VisibilitySubjectId!.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the audience visibility of every distinct subject across the batch with one batched lookup
    /// per (tenant, workspace, session) group (CORE-PERF-002), keyed by the full subject tuple so each event
    /// can read back its own subject's decision.
    /// </summary>
    private async Task<IReadOnlyDictionary<SubjectLookupKey, AudienceVisibility>> ResolveAudienceBatchAsync(
        IReadOnlyList<SessionEvent> sessionEvents,
        CancellationToken cancellationToken)
    {
        // Distinct subject-bearing, non-host-only events, grouped by (tenant, workspace, session). All events
        // of a replayed session share one scope, so this is a single group in practice; grouping keeps the
        // batch correct (and session-scoped) even if ever fed a mixed list.
        var subjectsByScope = new Dictionary<(Guid OrganizationId, Guid WorkspaceId, Guid SessionId), HashSet<(string SubjectType, Guid SubjectId)>>();
        foreach (var sessionEvent in sessionEvents)
        {
            if (!NeedsAudienceDecision(sessionEvent))
            {
                continue;
            }

            var scope = (sessionEvent.OrganizationId, sessionEvent.WorkspaceId, sessionEvent.SessionId);
            if (!subjectsByScope.TryGetValue(scope, out var subjects))
            {
                subjects = [];
                subjectsByScope[scope] = subjects;
            }

            subjects.Add((sessionEvent.VisibilitySubjectType!, sessionEvent.VisibilitySubjectId!.Value));
        }

        var resolved = new Dictionary<SubjectLookupKey, AudienceVisibility>();
        foreach (var (scope, subjects) in subjectsByScope)
        {
            var perSubject = await _visibility
                .ResolveAudienceRecipientsBatchAsync(
                    scope.OrganizationId, scope.WorkspaceId, scope.SessionId, subjects, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (subject, audience) in perSubject)
            {
                resolved[new SubjectLookupKey(
                    scope.OrganizationId, scope.WorkspaceId, scope.SessionId, subject.SubjectType, subject.SubjectId)] = audience;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Builds the recipient deliveries of an event from its already-resolved <paramref name="audience"/>
    /// visibility — the SINGLE routing implementation shared by <see cref="ResolveAsync"/> (which resolves
    /// the audience per event) and <see cref="ResolveBatchAsync"/> (which resolves it for the whole batch in
    /// one lookup). Keeping the routing in one place is what guarantees live delivery and reconnect replay
    /// produce the IDENTICAL recipient set (threat T3; docs/05_MODULE_CONTRACTS.md). The
    /// <paramref name="audience"/> is consulted only for a subject-bearing, non-host-only event; for the
    /// other classes the branches below decide without it.
    /// </summary>
    private static IReadOnlyList<SessionEventDelivery> BuildDeliveries(SessionEvent sessionEvent, AudienceVisibility audience)
    {
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
            // groups are reserved for selected-participant events (CORE-PERF-001). The per-participant
            // decision is derived from the SAME audience resolution — visible to them iff an audience-wide
            // visible rule, or a rule scoped to exactly them, exists (the same outcome as the central
            // CanParticipantReceive decision) — and an event with no visibility subject is unconditional.
            var canReceive = !sessionEvent.HasVisibilitySubject
                || audience.AudienceVisible
                || audience.SelectedVisibleParticipantIds.Contains(selectedParticipantId);
            if (canReceive)
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
    /// Whether an event needs an audience-visibility decision at all: only a subject-bearing event that is
    /// not host-only is gated through the Visibility engine. A host-only or no-subject event is routed
    /// without a rule lookup, so neither contributes a subject to the (single or batched) lookup.
    /// </summary>
    private static bool NeedsAudienceDecision(SessionEvent sessionEvent)
        => sessionEvent.HasVisibilitySubject && !SessionEventTypes.IsHostOnly(sessionEvent.EventType);

    /// <summary>The batched-resolution lookup key for an event's subject (scope + subject identifiers).</summary>
    private static SubjectLookupKey SubjectKey(SessionEvent sessionEvent)
        => new(
            sessionEvent.OrganizationId,
            sessionEvent.WorkspaceId,
            sessionEvent.SessionId,
            sessionEvent.VisibilitySubjectType!,
            sessionEvent.VisibilitySubjectId!.Value);

    /// <summary>
    /// Key for the batched audience-visibility map: the tenant/workspace/session scope plus the subject
    /// (type-name, id). Scoping by the full tuple keeps the batch correct even across a mixed event list.
    /// </summary>
    private readonly record struct SubjectLookupKey(
        Guid OrganizationId,
        Guid WorkspaceId,
        Guid SessionId,
        string SubjectType,
        Guid SubjectId);
}
