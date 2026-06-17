// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventRecipientResolver"/> (CORE-RT-004 projection; CORE-PERF-001 collapse).
/// This is the central anti-leak proof for "Realtime delivery never leaks hidden events" (threat T3) AND
/// the scale proof for CORE-PERF-001: given a stored event, the resolver computes exactly which
/// server-managed groups receive it and WHICH projection (host vs audience) each gets — delivering an
/// audience-wide event the whole audience may see to the SHARED session-audience group with ONE resolution
/// (not a per-participant fan-out), and reserving per-participant lookups/groups for selected-participant
/// events.
///
/// The visibility decision is FAKED, so the resolver's routing, projection and gating are exercised
/// deterministically and in isolation (the real Visibility decision is covered by
/// <see cref="LiveCore.Api.UnitTests.Visibility.EventRecipientVisibilityTests"/> and the policy tests). The
/// fake also COUNTS how many times the audience resolution runs, so the "one rule lookup, not N" property is
/// asserted directly. Group names come from <see cref="RealtimeGroups"/>. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventRecipientResolverTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid _org = Guid.NewGuid();
    private static readonly Guid _workspace = Guid.NewGuid();
    private static readonly Guid _session = Guid.NewGuid();

    private static SessionEvent AudienceEvent()
        => SessionEvent.Create(
            _org, _workspace, _session, SessionEventTypes.ContentRevealed, Guid.NewGuid(),
            targetParticipantId: null, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());

    private static SessionEvent SelectedEvent(Guid target)
        => SessionEvent.Create(
            _org, _workspace, _session, SessionEventTypes.ContentRevealed, Guid.NewGuid(),
            targetParticipantId: target, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());

    private static SessionEvent HostOnlyEvent(string eventType)
        => SessionEvent.Create(
            _org, _workspace, _session, eventType, Guid.NewGuid(),
            targetParticipantId: null, "{\"sessionId\":\"x\"}", 1, _now);

    [Fact]
    public async Task A_selected_event_reaches_only_the_selected_participant_and_hosts()
    {
        var selected = Guid.NewGuid();
        var visibility = new FakeRecipientVisibility(); // everyone visible
        var resolver = new SessionEventRecipientResolver(visibility);
        var sessionEvent = SelectedEvent(selected);

        var deliveries = await resolver.ResolveAsync(sessionEvent, CancellationToken.None);

        // Exactly the hosts group (host projection) and the SELECTED participant's group (audience
        // projection). NOT the observers group, NOT the shared audience group, NOT any other participant
        // group (the crown jewel). Selected events use the per-participant gate/group (CORE-PERF-001).
        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionParticipant(_session, selected),
            },
            deliveries.Select(delivery => delivery.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionAudience(_session), deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionObservers(_session), deliveries.Select(d => d.Group));

        // The hosts projection carries the routing target (confirmation); the participant projection does
        // NOT (a recipient never learns who else was targeted).
        Assert.Equal(selected, deliveries[0].Envelope.TargetParticipantId);
        Assert.Null(deliveries[1].Envelope.TargetParticipantId);
    }

    [Fact]
    public async Task A_selected_event_the_participant_may_not_see_reaches_hosts_only()
    {
        // Defence in depth: even on the selected path, the participant delivery is gated by the Visibility
        // engine. The per-participant decision is derived from the SAME audience resolution the audience path
        // uses (CORE-PERF-002, so the slice can be batched): the participant may see it iff an audience-wide
        // visible rule, or a rule scoped to exactly them, exists. Here neither holds, so only hosts receive
        // the (confirmation) event.
        var selected = Guid.NewGuid();
        var visibility = new FakeRecipientVisibility { AudienceVisible = false };
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(SelectedEvent(selected), CancellationToken.None);

        Assert.Equal(new[] { RealtimeGroups.SessionHosts(_session) }, deliveries.Select(d => d.Group));
    }

    [Fact]
    public async Task A_selected_event_reaches_a_participant_entitled_only_by_a_participant_scoped_rule()
    {
        // The audience-at-large may NOT see the subject, but the selected participant has a participant-scoped
        // visible rule (a selected-participant reveal on the same resource). They are entitled, so the event
        // reaches their own group (plus hosts) — derived from the SAME audience resolution, the exact outcome
        // the central per-participant decision (CanParticipantReceive) would give.
        var selected = Guid.NewGuid();
        var visibility = new FakeRecipientVisibility
        {
            AudienceVisible = false,
            SelectedVisibleParticipantIds = { selected },
        };
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(SelectedEvent(selected), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionParticipant(_session, selected),
            },
            deliveries.Select(delivery => delivery.Group));
    }

    [Fact]
    public async Task An_audience_wide_visible_event_reaches_hosts_observers_and_the_shared_audience_group_with_one_lookup()
    {
        // CORE-PERF-001: an audience-wide event the whole audience may see is delivered to hosts + observers
        // + the SHARED session-audience group — ONE resolution, NOT one per participant — so the recipient
        // set (and the lookup/publish count) is independent of audience size. No individual participant
        // group appears, even though many participants are individually entitled.
        var visibility = new FakeRecipientVisibility
        {
            AudienceVisible = true,
            // Many individually-entitled participants — all covered by the shared group, none delivered to
            // individually, so the delivery count does not grow with them.
            SelectedVisibleParticipantIds = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() },
        };
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionObservers(_session),
                RealtimeGroups.SessionAudience(_session),
            },
            deliveries.Select(delivery => delivery.Group));

        // THE scale assertion: exactly ONE audience resolution (one rule lookup), regardless of audience size.
        Assert.Equal(1, visibility.AudienceResolveCalls);
        // No per-participant gate is consulted on the audience path (that is reserved for selected events).
        Assert.Equal(0, visibility.ParticipantGateCalls);
        Assert.DoesNotContain(deliveries, d => d.Group.Contains(":participant:", StringComparison.Ordinal));

        // An audience-wide event has no routing target, so no projection carries one.
        Assert.All(deliveries, delivery => Assert.Null(delivery.Envelope.TargetParticipantId));
    }

    [Fact]
    public async Task An_audience_wide_event_the_audience_may_not_see_still_reaches_individually_entitled_participants()
    {
        // The audience-at-large may NOT see the subject (no audience-wide visible rule), but two participants
        // have a participant-scoped visible rule (a selected-participant reveal on the same resource). The
        // resolver delivers the audience-wide event to EXACTLY those participants' own groups (plus hosts) —
        // derived from the SAME single lookup — and to NEITHER the observers NOR the shared audience group,
        // so a participant who may not see it never receives it (threat T3).
        var entitledOne = Guid.NewGuid();
        var entitledTwo = Guid.NewGuid();
        var visibility = new FakeRecipientVisibility
        {
            AudienceVisible = false,
            SelectedVisibleParticipantIds = { entitledOne, entitledTwo },
        };
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionParticipant(_session, entitledOne),
                RealtimeGroups.SessionParticipant(_session, entitledTwo),
            },
            deliveries.Select(delivery => delivery.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionObservers(_session), deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionAudience(_session), deliveries.Select(d => d.Group));
        // Still one lookup — the individually-entitled set came from it, not from per-participant queries.
        Assert.Equal(1, visibility.AudienceResolveCalls);
        Assert.All(deliveries, delivery => Assert.Null(delivery.Envelope.TargetParticipantId));
    }

    [Fact]
    public async Task An_audience_wide_event_no_one_may_see_reaches_hosts_only()
    {
        // Neither an audience-wide visible rule nor any participant-scoped visible rule: only the hosts
        // (host-content roles see everything) receive it — no observers, no shared audience group, no
        // participant group.
        var visibility = new FakeRecipientVisibility { AudienceVisible = false };
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(new[] { RealtimeGroups.SessionHosts(_session) }, deliveries.Select(d => d.Group));
        Assert.Equal(1, visibility.AudienceResolveCalls);
    }

    [Fact]
    public async Task An_event_with_no_visibility_subject_is_delivered_unconditionally()
    {
        // An event with no subject (e.g. a later SessionStarted) is not gated: the hosts, observers and the
        // whole session audience (the shared group) receive it, and the Visibility engine is never consulted.
        var visibility = new FakeRecipientVisibility();
        var resolver = new SessionEventRecipientResolver(visibility);
        var subjectless = SessionEvent.Create(
            _org, _workspace, _session, "SessionStarted", Guid.NewGuid(), targetParticipantId: null, "{}", 1, _now);

        var deliveries = await resolver.ResolveAsync(subjectless, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionObservers(_session),
                RealtimeGroups.SessionAudience(_session),
            },
            deliveries.Select(delivery => delivery.Group));
        Assert.False(visibility.WasConsulted);
    }

    [Theory]
    [InlineData(SessionEventTypes.SessionCreated)]
    [InlineData(SessionEventTypes.RecapGenerated)]
    public async Task A_host_only_event_reaches_the_hosts_group_only_and_never_the_audience(string eventType)
    {
        // CORE-EVT-004: a host-only preparation/output event (SessionCreated, RecapGenerated) reaches the
        // session hosts group and STOPS — never an observer, never the shared audience group, never a
        // participant — even with full visibility. The Visibility engine is never consulted (the routing is
        // subject-independent), so the audience can never receive it, live or on replay (threats T2/T7).
        var visibility = new FakeRecipientVisibility(); // everyone would otherwise be visible
        var resolver = new SessionEventRecipientResolver(visibility);

        var deliveries = await resolver.ResolveAsync(HostOnlyEvent(eventType), CancellationToken.None);

        Assert.Equal(new[] { RealtimeGroups.SessionHosts(_session) }, deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionObservers(_session), deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionAudience(_session), deliveries.Select(d => d.Group));
        Assert.False(visibility.WasConsulted);
    }

    [Fact]
    public async Task It_rejects_a_null_event()
    {
        var resolver = new SessionEventRecipientResolver(new FakeRecipientVisibility());
        await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, CancellationToken.None));
    }

    // --- Test doubles ----------------------------------------------------------

    private sealed class FakeRecipientVisibility : IEventRecipientVisibility
    {
        /// <summary>Whether the audience-at-large may see the audience event's subject.</summary>
        public bool AudienceVisible { get; set; } = true;

        /// <summary>Participants made visible only by a participant-scoped rule (the audience-hidden case).</summary>
        public List<Guid> SelectedVisibleParticipantIds { get; } = [];

        /// <summary>Participants the per-participant gate (the selected-event path) treats as unable to see.</summary>
        public HashSet<Guid> HiddenParticipants { get; } = [];

        /// <summary>How many times the audience resolution ran — the "one rule lookup, not N" counter.</summary>
        public int AudienceResolveCalls { get; private set; }

        /// <summary>How many times the per-participant gate ran (only the selected-event path uses it).</summary>
        public int ParticipantGateCalls { get; private set; }

        public bool WasConsulted => AudienceResolveCalls > 0 || ParticipantGateCalls > 0;

        public Task<AudienceVisibility> ResolveAudienceRecipientsAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
        {
            AudienceResolveCalls++;
            return Task.FromResult(new AudienceVisibility(AudienceVisible, SelectedVisibleParticipantIds.ToArray()));
        }

        public Task<bool> CanParticipantReceiveAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            Guid participantId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
        {
            ParticipantGateCalls++;
            return Task.FromResult(!HiddenParticipants.Contains(participantId));
        }

        // The batched audience resolution (CORE-PERF-002): each subject resolves to the same fixed audience
        // decision the single resolution returns, counted as one resolution per call (the batch is one
        // lookup over the slice's distinct subjects).
        public Task<IReadOnlyDictionary<(string SubjectType, Guid SubjectId), AudienceVisibility>>
            ResolveAudienceRecipientsBatchAsync(
                Guid organizationId,
                Guid workspaceId,
                Guid sessionId,
                IReadOnlyCollection<(string SubjectType, Guid SubjectId)> subjects,
                CancellationToken cancellationToken)
        {
            AudienceResolveCalls++;
            var audience = new AudienceVisibility(AudienceVisible, SelectedVisibleParticipantIds.ToArray());
            IReadOnlyDictionary<(string SubjectType, Guid SubjectId), AudienceVisibility> result =
                subjects.Distinct().ToDictionary(subject => subject, _ => audience);
            return Task.FromResult(result);
        }
    }
}
