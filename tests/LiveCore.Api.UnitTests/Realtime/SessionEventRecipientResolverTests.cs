using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventRecipientResolver"/> (CORE-RT-004, "recipient-specific event
/// projection"). This is the central anti-leak proof for the story's acceptance ("Realtime delivery never
/// leaks hidden events", threat T3): given a stored event, the resolver computes exactly which
/// server-managed groups receive it and WHICH projection (host vs audience) each gets, fanning an
/// audience-wide event out to the active participants and gating every recipient through the central
/// Visibility engine.
///
/// The visibility decision and the active-participant list are FAKED, so the resolver's routing,
/// projection and per-recipient gating are exercised deterministically and in isolation (the real
/// Visibility decision is covered by <see cref="LiveCore.Api.UnitTests.Visibility.EventRecipientVisibilityTests"/>
/// and the policy tests). Group names come from <see cref="RealtimeGroups"/>. All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class SessionEventRecipientResolverTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid _org = Guid.NewGuid();
    private static readonly Guid _workspace = Guid.NewGuid();
    private static readonly Guid _session = Guid.NewGuid();

    private static Participant NewParticipant()
        => Participant.Create(_org, _workspace, userProfileId: null, "Guest", _now);

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

    [Fact]
    public async Task A_selected_event_reaches_only_the_selected_participant_and_hosts()
    {
        var selected = NewParticipant();
        var other = NewParticipant();
        var visibility = new FakeRecipientVisibility(); // everyone visible
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(selected, other));
        var sessionEvent = SelectedEvent(selected.Id);

        var deliveries = await resolver.ResolveAsync(sessionEvent, CancellationToken.None);

        // Exactly the hosts group (host projection) and the SELECTED participant's group (audience
        // projection). NOT the observers group, NOT the other participant's group (the crown jewel).
        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionParticipant(_session, selected.Id),
            },
            deliveries.Select(delivery => delivery.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionParticipant(_session, other.Id), deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionObservers(_session), deliveries.Select(d => d.Group));

        // The hosts projection carries the routing target (confirmation); the participant projection does
        // NOT (a recipient never learns who else was targeted).
        Assert.Equal(selected.Id, deliveries[0].Envelope.TargetParticipantId);
        Assert.Null(deliveries[1].Envelope.TargetParticipantId);
    }

    [Fact]
    public async Task A_selected_event_the_participant_may_not_see_reaches_hosts_only()
    {
        // Defence in depth: even on the selected path, the participant delivery is gated by the Visibility
        // engine. If they may not see the subject, only hosts receive the (confirmation) event.
        var selected = NewParticipant();
        var visibility = new FakeRecipientVisibility { HiddenParticipants = { selected.Id } };
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(selected));

        var deliveries = await resolver.ResolveAsync(SelectedEvent(selected.Id), CancellationToken.None);

        Assert.Equal(new[] { RealtimeGroups.SessionHosts(_session) }, deliveries.Select(d => d.Group));
    }

    [Fact]
    public async Task An_audience_wide_event_fans_out_to_hosts_observers_and_every_visible_participant()
    {
        var a = NewParticipant();
        var b = NewParticipant();
        var visibility = new FakeRecipientVisibility(); // audience + all participants visible
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(a, b));

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionObservers(_session),
                RealtimeGroups.SessionParticipant(_session, a.Id),
                RealtimeGroups.SessionParticipant(_session, b.Id),
            },
            deliveries.Select(delivery => delivery.Group));

        // An audience-wide event has no routing target, so no projection carries one.
        Assert.All(deliveries, delivery => Assert.Null(delivery.Envelope.TargetParticipantId));
    }

    [Fact]
    public async Task An_audience_wide_event_never_reaches_a_participant_who_may_not_see_it()
    {
        // THE fan-out anti-leak proof: participant B may not see the subject, so B's group is filtered out
        // of the deliveries entirely — the resolver never sends a hidden event to B (threat T3).
        var a = NewParticipant();
        var b = NewParticipant();
        var visibility = new FakeRecipientVisibility { HiddenParticipants = { b.Id } };
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(a, b));

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.Contains(RealtimeGroups.SessionParticipant(_session, a.Id), deliveries.Select(d => d.Group));
        Assert.DoesNotContain(RealtimeGroups.SessionParticipant(_session, b.Id), deliveries.Select(d => d.Group));
    }

    [Fact]
    public async Task An_audience_wide_event_gates_observers_independently_of_participants()
    {
        // The observers (audience-at-large) gate is independent of the per-participant gate: if the
        // audience may not see the subject, the observers group is omitted even when a specific
        // participant may still see it (e.g. via a participant-scoped rule).
        var a = NewParticipant();
        var visibility = new FakeRecipientVisibility { AudienceVisible = false }; // a still visible per-participant
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(a));

        var deliveries = await resolver.ResolveAsync(AudienceEvent(), CancellationToken.None);

        Assert.DoesNotContain(RealtimeGroups.SessionObservers(_session), deliveries.Select(d => d.Group));
        Assert.Contains(RealtimeGroups.SessionParticipant(_session, a.Id), deliveries.Select(d => d.Group));
        Assert.Contains(RealtimeGroups.SessionHosts(_session), deliveries.Select(d => d.Group));
    }

    [Fact]
    public async Task An_event_with_no_visibility_subject_is_delivered_unconditionally()
    {
        // An event with no subject (e.g. a later SessionStarted) is not gated: the audience and all active
        // participants receive it, and the Visibility engine is never consulted.
        var a = NewParticipant();
        var visibility = new FakeRecipientVisibility();
        var resolver = new SessionEventRecipientResolver(visibility, new FakeParticipantRepository(a));
        var subjectless = SessionEvent.Create(
            _org, _workspace, _session, "SessionStarted", Guid.NewGuid(), targetParticipantId: null, "{}", 1, _now);

        var deliveries = await resolver.ResolveAsync(subjectless, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(_session),
                RealtimeGroups.SessionObservers(_session),
                RealtimeGroups.SessionParticipant(_session, a.Id),
            },
            deliveries.Select(delivery => delivery.Group));
        Assert.False(visibility.WasConsulted);
    }

    [Fact]
    public async Task It_rejects_a_null_event()
    {
        var resolver = new SessionEventRecipientResolver(new FakeRecipientVisibility(), new FakeParticipantRepository());
        await Assert.ThrowsAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, CancellationToken.None));
    }

    // --- Test doubles ----------------------------------------------------------

    private sealed class FakeRecipientVisibility : IEventRecipientVisibility
    {
        public HashSet<Guid> HiddenParticipants { get; } = [];

        public bool AudienceVisible { get; set; } = true;

        public bool WasConsulted { get; private set; }

        public Task<bool> CanAudienceReceiveAsync(
            Guid organizationId,
            Guid workspaceId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
        {
            WasConsulted = true;
            return Task.FromResult(AudienceVisible);
        }

        public Task<bool> CanParticipantReceiveAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid participantId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
        {
            WasConsulted = true;
            return Task.FromResult(!HiddenParticipants.Contains(participantId));
        }
    }

    private sealed class FakeParticipantRepository : IParticipantRepository
    {
        private readonly IReadOnlyList<Participant> _active;

        public FakeParticipantRepository(params Participant[] active) => _active = active;

        public Task<IReadOnlyList<Participant>> ListActiveByWorkspaceAsync(
            Guid organizationId,
            Guid workspaceId,
            CancellationToken cancellationToken)
            => Task.FromResult(_active);

        public Task<Participant?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Participant?> FindByIdInOrganizationAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Participant?> FindByUserAsync(Guid organizationId, Guid workspaceId, Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ParticipantAddResult> AddAsync(Participant participant, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateAsync(Participant participant, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
