// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Participants;
using LiveCore.Api.Realtime;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventPushRecipientResolver"/> (CORE-PUSH-002). It proves the closed-app push
/// audience EQUALS the in-app realtime audience — resolved through the SAME central Visibility gate and mapped to the
/// recipient participants' linked users — and NEVER wider (threats T2/T3): a host-only event has no participant
/// recipients, a selected-participant event reaches only its authorized target, an audience-wide event reaches every
/// active participant only when the audience may see it, an audience-hidden event still reaches participants entitled
/// by a participant-scoped rule, and anonymous participants (no user link) are omitted.
///
/// The visibility decision and the participant store are FAKED, so the resolver's routing is exercised
/// deterministically and in isolation. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventPushRecipientResolverTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 21, 9, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task An_audience_wide_visible_event_reaches_every_active_participants_user()
    {
        var participants = new FakeParticipantRepository();
        var userA = participants.AddLinked();
        var userB = participants.AddLinked();
        participants.AddAnonymous(); // an anonymous participant cannot be pushed
        var visibility = new FakeRecipientVisibility { AudienceVisible = true };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(new HashSet<Guid> { userA, userB }, recipients.ToHashSet());
    }

    [Fact]
    public async Task An_audience_wide_hidden_event_reaches_no_one_when_no_participant_scoped_rule_exists()
    {
        var participants = new FakeParticipantRepository();
        participants.AddLinked();
        participants.AddLinked();
        var visibility = new FakeRecipientVisibility { AudienceVisible = false };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(AudienceEvent(), CancellationToken.None);

        Assert.Empty(recipients);
    }

    [Fact]
    public async Task An_audience_hidden_event_still_reaches_a_participant_scoped_recipient()
    {
        var participants = new FakeParticipantRepository();
        var entitledUser = participants.AddLinked(out var entitledParticipantId);
        participants.AddLinked(); // an unentitled participant is NOT a recipient
        var visibility = new FakeRecipientVisibility
        {
            AudienceVisible = false,
            SelectedVisibleParticipantIds = { entitledParticipantId },
        };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(new[] { entitledUser }, recipients);
    }

    [Fact]
    public async Task A_selected_event_reaches_only_the_authorized_target_user()
    {
        var participants = new FakeParticipantRepository();
        var targetUser = participants.AddLinked(out var targetParticipantId);
        participants.AddLinked(); // another participant must NOT receive a private reveal
        var visibility = new FakeRecipientVisibility
        {
            AudienceVisible = false,
            SelectedVisibleParticipantIds = { targetParticipantId },
        };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(
            SelectedEvent(targetParticipantId), CancellationToken.None);

        Assert.Equal(new[] { targetUser }, recipients);
    }

    [Fact]
    public async Task A_selected_event_the_target_may_not_see_reaches_no_one()
    {
        var participants = new FakeParticipantRepository();
        participants.AddLinked(out var targetParticipantId);
        var visibility = new FakeRecipientVisibility { AudienceVisible = false }; // not visible, not scoped to them
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(
            SelectedEvent(targetParticipantId), CancellationToken.None);

        Assert.Empty(recipients);
    }

    [Theory]
    [InlineData(SessionEventTypes.SessionCreated)]
    [InlineData(SessionEventTypes.RecapGenerated)]
    public async Task A_host_only_event_produces_no_push_recipients_and_never_consults_visibility(string eventType)
    {
        var participants = new FakeParticipantRepository();
        participants.AddLinked();
        var visibility = new FakeRecipientVisibility { AudienceVisible = true };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);
        var hostOnly = SessionEvent.Create(
            _org, _workspace, _session, eventType, Guid.NewGuid(), targetParticipantId: null, "{}", 1, _now);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(hostOnly, CancellationToken.None);

        Assert.Empty(recipients);
        Assert.False(visibility.WasConsulted);
        Assert.False(participants.WasListed);
    }

    [Fact]
    public async Task An_event_with_no_visibility_subject_reaches_every_active_participants_user()
    {
        var participants = new FakeParticipantRepository();
        var userA = participants.AddLinked();
        var userB = participants.AddLinked();
        var visibility = new FakeRecipientVisibility();
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);
        var subjectless = SessionEvent.Create(
            _org, _workspace, _session, "SessionStarted", Guid.NewGuid(), targetParticipantId: null, "{}", 1, _now);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(subjectless, CancellationToken.None);

        Assert.Equal(new HashSet<Guid> { userA, userB }, recipients.ToHashSet());
        Assert.False(visibility.WasConsulted); // no subject -> no visibility lookup
    }

    [Fact]
    public async Task A_user_with_several_participant_records_is_returned_once()
    {
        var participants = new FakeParticipantRepository();
        var sharedUser = Guid.NewGuid();
        participants.AddLinkedFor(sharedUser);
        participants.AddLinkedFor(sharedUser);
        var visibility = new FakeRecipientVisibility { AudienceVisible = true };
        var resolver = new SessionEventPushRecipientResolver(visibility, participants);

        var recipients = await resolver.ResolveRecipientUserIdsAsync(AudienceEvent(), CancellationToken.None);

        Assert.Equal(new[] { sharedUser }, recipients);
    }

    [Fact]
    public async Task It_rejects_a_null_event()
    {
        var resolver = new SessionEventPushRecipientResolver(new FakeRecipientVisibility(), new FakeParticipantRepository());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => resolver.ResolveRecipientUserIdsAsync(null!, CancellationToken.None));
    }

    // --- Test doubles ----------------------------------------------------------

    private sealed class FakeRecipientVisibility : IEventRecipientVisibility
    {
        public bool AudienceVisible { get; set; }

        public List<Guid> SelectedVisibleParticipantIds { get; } = [];

        public bool WasConsulted { get; private set; }

        public Task<AudienceVisibility> ResolveAudienceRecipientsAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
        {
            WasConsulted = true;
            return Task.FromResult(new AudienceVisibility(AudienceVisible, SelectedVisibleParticipantIds.ToArray()));
        }

        public Task<IReadOnlyDictionary<(string SubjectType, Guid SubjectId), AudienceVisibility>>
            ResolveAudienceRecipientsBatchAsync(
                Guid organizationId,
                Guid workspaceId,
                Guid sessionId,
                IReadOnlyCollection<(string SubjectType, Guid SubjectId)> subjects,
                CancellationToken cancellationToken)
            => throw new NotSupportedException("The push recipient resolver uses only the single-subject resolution.");

        public Task<bool> CanParticipantReceiveAsync(
            Guid organizationId,
            Guid workspaceId,
            Guid sessionId,
            Guid participantId,
            string subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The push recipient resolver derives the per-participant decision from the audience resolution.");
    }

    private sealed class FakeParticipantRepository : IParticipantRepository
    {
        private readonly List<Participant> _participants = [];

        public bool WasListed { get; private set; }

        public Guid AddLinked() => AddLinked(out _);

        public Guid AddLinked(out Guid participantId)
        {
            var userId = Guid.NewGuid();
            participantId = AddLinkedFor(userId);
            return userId;
        }

        public Guid AddLinkedFor(Guid userId)
        {
            var participant = Participant.Create(_org, _workspace, userId, "P", _now);
            _participants.Add(participant);
            return participant.Id;
        }

        public void AddAnonymous()
            => _participants.Add(Participant.Create(_org, _workspace, userProfileId: null, "Guest", _now));

        public Task<IReadOnlyList<Participant>> ListActiveByWorkspaceAsync(
            Guid organizationId,
            Guid workspaceId,
            CancellationToken cancellationToken)
        {
            WasListed = true;
            Assert.Equal(_org, organizationId);
            Assert.Equal(_workspace, workspaceId);
            return Task.FromResult<IReadOnlyList<Participant>>(_participants.ToArray());
        }

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

        public Task<int> AnonymizeBySubjectAsync(Guid userProfileId, DateTimeOffset updatedAt, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Participant>> ListBySubjectInOrganizationAsync(Guid organizationId, Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
