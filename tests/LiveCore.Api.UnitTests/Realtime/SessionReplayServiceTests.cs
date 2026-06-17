// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionReplayService"/> (CORE-RT-005, "reconnect replay with filtering"). They pin
/// the two things the replay filter is responsible for, in isolation: (1) the CURSOR slice — only events
/// after the last acknowledged id, in append order — and (2) the per-recipient INTERSECT — keep only the
/// delivery addressed to one of the caller's own server-managed groups, with the projection that group
/// received. The per-event gating/projection itself is the recipient resolver's job (covered by
/// <see cref="SessionEventRecipientResolverTests"/>), so here it is FAKED to return controlled deliveries,
/// which is exactly what lets these tests prove the slice + intersect deterministically.
///
/// The headline security property — a host replays all events, a selected participant replays its own
/// private events plus audience events, and an UNSELECTED participant never replays another's private
/// event (threat T3) — is asserted end-to-end over real HTTP with the real Visibility engine in
/// <c>SessionEventReplayEndpointTests</c>; these unit tests prove the filter glue that delivers it. All
/// fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionReplayServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid _org = Guid.NewGuid();
    private static readonly Guid _workspace = Guid.NewGuid();
    private static readonly Guid _session = Guid.NewGuid();

    private static readonly string _hostsGroup = RealtimeGroups.SessionHosts(_session);
    private static readonly string _observersGroup = RealtimeGroups.SessionObservers(_session);

    private static SessionEvent NewEvent(long sequence, Guid? target = null)
    {
        // The per-session sequence (CORE-RTC-001) is normally stamped by the append path; the fake repository
        // does not append, so the tests assign it explicitly to drive the sequence-based cursor slice.
        var sessionEvent = SessionEvent.Create(
            _org, _workspace, _session, SessionEventTypes.ContentRevealed, Guid.NewGuid(),
            target, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());
        sessionEvent.AssignSequence(sequence);
        return sessionEvent;
    }

    [Fact]
    public async Task A_host_replays_every_event_with_the_host_projection()
    {
        // The fake resolver always delivers each event to the hosts group with the host projection. A host
        // (in the hosts group) therefore replays every event, carrying the routing target confirmation.
        var first = NewEvent(1);
        var selected = NewEvent(2, target: Guid.NewGuid());
        var resolver = new FakeRecipientResolver(HostAlways);
        var service = new SessionReplayService(new FakeEventRepository(first, selected), resolver);

        var replay = await service.ReplayAsync(
            _org, _session, [_hostsGroup], afterSequence: null, CancellationToken.None);

        Assert.Equal(new[] { first.Id, selected.Id }, replay.Select(envelope => envelope.EventId));
        // Host projection carries the routing target for the selected event.
        Assert.Equal(selected.TargetParticipantId, replay[1].TargetParticipantId);
    }

    [Fact]
    public async Task A_participant_replays_only_the_deliveries_addressed_to_their_own_group()
    {
        // Two events: one selected to participant A, one audience-wide. The resolver delivers the selected
        // event to A's group only (plus hosts) and the audience event to BOTH participants. Participant A
        // replays both; participant B replays only the audience event — B never replays A's private event
        // (the crown jewel, threat T3).
        var participantA = Guid.NewGuid();
        var participantB = Guid.NewGuid();
        var groupA = RealtimeGroups.SessionParticipant(_session, participantA);
        var groupB = RealtimeGroups.SessionParticipant(_session, participantB);

        var selectedToA = NewEvent(1, target: participantA);
        var audience = NewEvent(2);
        var resolver = new FakeRecipientResolver(sessionEvent => sessionEvent.Id == selectedToA.Id
            ? [Delivery(_hostsGroup, SessionEventEnvelope.ForHost(sessionEvent)), Delivery(groupA, SessionEventEnvelope.ForAudience(sessionEvent))]
            : [Delivery(_hostsGroup, SessionEventEnvelope.ForHost(sessionEvent)), Delivery(groupA, SessionEventEnvelope.ForAudience(sessionEvent)), Delivery(groupB, SessionEventEnvelope.ForAudience(sessionEvent))]);
        var service = new SessionReplayService(new FakeEventRepository(selectedToA, audience), resolver);

        var replayA = await service.ReplayAsync(_org, _session, [groupA], afterSequence: null, CancellationToken.None);
        var replayB = await service.ReplayAsync(_org, _session, [groupB], afterSequence: null, CancellationToken.None);

        Assert.Equal(new[] { selectedToA.Id, audience.Id }, replayA.Select(envelope => envelope.EventId));
        Assert.Equal(new[] { audience.Id }, replayB.Select(envelope => envelope.EventId));
        // The audience projection a participant replays never carries the routing target.
        Assert.All(replayB, envelope => Assert.Null(envelope.TargetParticipantId));
    }

    [Fact]
    public async Task An_observer_does_not_replay_a_selected_event()
    {
        // The resolver delivers a selected event to hosts + the selected participant only (never observers).
        // An observer's group is in neither delivery, so the observer replays nothing.
        var participant = Guid.NewGuid();
        var selected = NewEvent(1, target: participant);
        var resolver = new FakeRecipientResolver(sessionEvent =>
        [
            Delivery(_hostsGroup, SessionEventEnvelope.ForHost(sessionEvent)),
            Delivery(RealtimeGroups.SessionParticipant(_session, participant), SessionEventEnvelope.ForAudience(sessionEvent)),
        ]);
        var service = new SessionReplayService(new FakeEventRepository(selected), resolver);

        var replay = await service.ReplayAsync(_org, _session, [_observersGroup], afterSequence: null, CancellationToken.None);

        Assert.Empty(replay);
    }

    [Fact]
    public async Task The_cursor_after_sequence_n_replays_only_n_plus_one_onwards()
    {
        // The required test: a cursor of sequence N returns N+1.. with no skips or duplicates (CORE-RTC-001).
        var first = NewEvent(1);
        var second = NewEvent(2);
        var third = NewEvent(3);
        var resolver = new FakeRecipientResolver(HostAlways);
        var service = new SessionReplayService(new FakeEventRepository(first, second, third), resolver);

        var replay = await service.ReplayAsync(
            _org, _session, [_hostsGroup], afterSequence: first.Sequence, CancellationToken.None);

        Assert.Equal(new[] { second.Id, third.Id }, replay.Select(envelope => envelope.EventId));
        Assert.Equal(new long[] { 2, 3 }, replay.Select(envelope => envelope.Sequence));
    }

    [Fact]
    public async Task The_cursor_at_the_last_sequence_replays_nothing()
    {
        var first = NewEvent(1);
        var second = NewEvent(2);
        var service = new SessionReplayService(new FakeEventRepository(first, second), new FakeRecipientResolver(HostAlways));

        var replay = await service.ReplayAsync(
            _org, _session, [_hostsGroup], afterSequence: second.Sequence, CancellationToken.None);

        Assert.Empty(replay);
    }

    [Fact]
    public async Task A_cursor_below_the_first_sequence_replays_the_whole_stream()
    {
        // A zero/out-of-range cursor is fail-safe: replay the whole stream (still per-recipient filtered),
        // and let the client deduplicate (docs/11 "duplicate event handling").
        var first = NewEvent(1);
        var second = NewEvent(2);
        var service = new SessionReplayService(new FakeEventRepository(first, second), new FakeRecipientResolver(HostAlways));

        var replay = await service.ReplayAsync(
            _org, _session, [_hostsGroup], afterSequence: 0, CancellationToken.None);

        Assert.Equal(new[] { first.Id, second.Id }, replay.Select(envelope => envelope.EventId));
    }

    [Fact]
    public async Task An_empty_group_set_replays_nothing_without_touching_the_repository()
    {
        var repository = new FakeEventRepository(NewEvent(1));
        var service = new SessionReplayService(repository, new FakeRecipientResolver(HostAlways));

        var replay = await service.ReplayAsync(_org, _session, [], afterSequence: null, CancellationToken.None);

        Assert.Empty(replay);
        Assert.False(repository.WasQueried);
    }

    [Fact]
    public async Task It_rejects_empty_ids_and_a_null_group_set()
    {
        var service = new SessionReplayService(new FakeEventRepository(), new FakeRecipientResolver(HostAlways));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplayAsync(Guid.Empty, _session, [_hostsGroup], null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplayAsync(_org, Guid.Empty, [_hostsGroup], null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ReplayAsync(_org, _session, null!, null, CancellationToken.None));
    }

    // --- Helpers / test doubles ------------------------------------------------

    private static SessionEventDelivery Delivery(string group, SessionEventEnvelope envelope)
        => new(group, envelope);

    private static IReadOnlyList<SessionEventDelivery> HostAlways(SessionEvent sessionEvent)
        => [Delivery(_hostsGroup, SessionEventEnvelope.ForHost(sessionEvent))];

    private sealed class FakeRecipientResolver : ISessionEventRecipientResolver
    {
        private readonly Func<SessionEvent, IReadOnlyList<SessionEventDelivery>> _plan;

        public FakeRecipientResolver(Func<SessionEvent, IReadOnlyList<SessionEventDelivery>> plan) => _plan = plan;

        public Task<IReadOnlyList<SessionEventDelivery>> ResolveAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken)
            => Task.FromResult(_plan(sessionEvent));
    }

    private sealed class FakeEventRepository : ISessionEventRepository
    {
        private readonly IReadOnlyList<SessionEvent> _stream;

        public FakeEventRepository(params SessionEvent[] stream) => _stream = stream;

        public bool WasQueried { get; private set; }

        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            Guid organizationId,
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            WasQueried = true;
            return Task.FromResult(_stream);
        }
    }
}
