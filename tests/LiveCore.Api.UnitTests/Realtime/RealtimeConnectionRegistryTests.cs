// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="RealtimeConnectionRegistry"/> (CORE-RTC-002) — the in-memory record of live hub
/// connections and the <see cref="IRealtimeConnectionEvictor"/> that re-authorizes them. Eviction aborts a
/// connection through the abort handle the hub registered (in production the connection's
/// <c>HubCallerContext.Abort</c>); here a recording handle stands in so the matching logic is exercised
/// deterministically without a live socket.
///
/// The story's required scenarios — "a participant removed mid-session receives no further events on the open
/// socket; a demoted host loses host-group deliveries" — reduce to: eviction aborts EXACTLY the affected
/// connections and never a bystander. So the suite is built around precision:
/// <list type="bullet">
///   <item>PARTICIPANT removal aborts only that participant's connection — never another participant, another
///   session/workspace/tenant, nor the same subject's separate host/observer connection.</item>
///   <item>MEMBER role change aborts only that subject's host/observer connections in that workspace — never
///   another member, another workspace/tenant, nor the subject's own participant connection (their participant
///   standing is unchanged).</item>
///   <item>FAIL-CLOSED isolation: a foreign-tenant / foreign-workspace / empty-id eviction aborts nothing, and
///   an unregistered (disconnected) connection is never aborted.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionRegistryTests
{
    private static readonly Guid _org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _workspace = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // --- Participant eviction ----------------------------------------------------

    [Fact]
    public async Task Evicting_a_participant_aborts_only_that_participants_connection()
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();
        var otherParticipantId = Guid.NewGuid();

        var target = Track(registry, "conn-target", ParticipantSubject(participantId, Guid.NewGuid()));
        var other = Track(registry, "conn-other", ParticipantSubject(otherParticipantId, Guid.NewGuid()));

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.True(target.Aborted);
        Assert.False(other.Aborted);
    }

    [Fact]
    public async Task Evicting_a_participant_does_not_abort_a_member_connection_of_the_same_subject()
    {
        // A subject may be both a participant AND a host/observer member. Removing their participant standing
        // must not tear down their member connection (and vice-versa): the two connections are evicted by
        // different triggers.
        var registry = new RealtimeConnectionRegistry();
        var userProfileId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        var participantConnection = Track(registry, "conn-participant", ParticipantSubject(participantId, userProfileId));
        var memberConnection = Track(registry, "conn-member", MemberSubject(userProfileId));

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.True(participantConnection.Aborted);
        Assert.False(memberConnection.Aborted);
    }

    [Theory]
    [InlineData(true, false, false)] // foreign tenant
    [InlineData(false, true, false)] // foreign workspace
    [InlineData(false, false, true)] // foreign session
    public async Task Evicting_a_participant_in_a_foreign_scope_aborts_nothing(bool foreignOrg, bool foreignWorkspace, bool foreignSession)
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();
        var connection = Track(registry, "conn", ParticipantSubject(participantId, Guid.NewGuid()));

        await registry.EvictParticipantAsync(
            foreignOrg ? Guid.NewGuid() : _org,
            foreignWorkspace ? Guid.NewGuid() : _workspace,
            foreignSession ? Guid.NewGuid() : _session,
            participantId,
            CancellationToken.None);

        Assert.False(connection.Aborted);
    }

    [Fact]
    public async Task Evicting_an_empty_participant_id_aborts_nothing()
    {
        var registry = new RealtimeConnectionRegistry();
        var connection = Track(registry, "conn", ParticipantSubject(Guid.NewGuid(), Guid.NewGuid()));

        await registry.EvictParticipantAsync(_org, _workspace, _session, Guid.Empty, CancellationToken.None);

        Assert.False(connection.Aborted);
    }

    // --- Member eviction ---------------------------------------------------------

    [Fact]
    public async Task Evicting_a_member_aborts_only_that_subjects_member_connections()
    {
        var registry = new RealtimeConnectionRegistry();
        var demoted = Guid.NewGuid();
        var otherMember = Guid.NewGuid();

        // The demoted subject holds two host/observer connections (e.g. two tabs); both are evicted.
        var host = Track(registry, "conn-host", MemberSubject(demoted));
        var secondTab = Track(registry, "conn-host-2", MemberSubject(demoted));
        var bystander = Track(registry, "conn-bystander", MemberSubject(otherMember));

        await registry.EvictWorkspaceMemberAsync(_org, _workspace, demoted, CancellationToken.None);

        Assert.True(host.Aborted);
        Assert.True(secondTab.Aborted);
        Assert.False(bystander.Aborted);
    }

    [Fact]
    public async Task Evicting_a_member_does_not_abort_the_same_subjects_participant_connection()
    {
        var registry = new RealtimeConnectionRegistry();
        var userProfileId = Guid.NewGuid();

        var memberConnection = Track(registry, "conn-member", MemberSubject(userProfileId));
        var participantConnection = Track(registry, "conn-participant", ParticipantSubject(Guid.NewGuid(), userProfileId));

        await registry.EvictWorkspaceMemberAsync(_org, _workspace, userProfileId, CancellationToken.None);

        Assert.True(memberConnection.Aborted);
        Assert.False(participantConnection.Aborted);
    }

    [Theory]
    [InlineData(true, false)] // foreign tenant
    [InlineData(false, true)] // foreign workspace
    public async Task Evicting_a_member_in_a_foreign_scope_aborts_nothing(bool foreignOrg, bool foreignWorkspace)
    {
        var registry = new RealtimeConnectionRegistry();
        var userProfileId = Guid.NewGuid();
        var connection = Track(registry, "conn", MemberSubject(userProfileId));

        await registry.EvictWorkspaceMemberAsync(
            foreignOrg ? Guid.NewGuid() : _org,
            foreignWorkspace ? Guid.NewGuid() : _workspace,
            userProfileId,
            CancellationToken.None);

        Assert.False(connection.Aborted);
    }

    [Fact]
    public async Task Evicting_an_empty_subject_id_aborts_nothing()
    {
        var registry = new RealtimeConnectionRegistry();
        var connection = Track(registry, "conn", MemberSubject(Guid.NewGuid()));

        await registry.EvictWorkspaceMemberAsync(_org, _workspace, Guid.Empty, CancellationToken.None);

        Assert.False(connection.Aborted);
    }

    // --- Lifecycle ---------------------------------------------------------------

    [Fact]
    public async Task An_unregistered_connection_is_not_aborted()
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();
        var connection = Track(registry, "conn", ParticipantSubject(participantId, Guid.NewGuid()));

        // The client disconnected (the hub unregistered it) before the removal command ran.
        registry.Unregister("conn");

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.False(connection.Aborted);
    }

    [Fact]
    public async Task A_connection_is_aborted_at_most_once_even_if_the_eviction_repeats()
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();
        var connection = Track(registry, "conn", ParticipantSubject(participantId, Guid.NewGuid()));

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);
        // A repeat (e.g. an idempotent retry) finds the entry already removed and aborts nothing further.
        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.Equal(1, connection.AbortCount);
    }

    [Fact]
    public async Task A_throwing_abort_does_not_stop_the_rest_of_the_eviction()
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();

        // One connection's abort throws (its socket was already torn down); a second matching connection must
        // still be aborted — one dead connection can never shield another from re-authorization.
        var faulting = TrackFaulting(registry, "conn-faulting", ParticipantSubject(participantId, Guid.NewGuid()));
        var healthy = Track(registry, "conn-healthy", ParticipantSubject(participantId, Guid.NewGuid()));

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.True(faulting.AbortAttempted);
        Assert.True(healthy.Aborted);
    }

    [Fact]
    public void Register_rejects_a_blank_connection_id_or_null_abort()
    {
        var registry = new RealtimeConnectionRegistry();

        Assert.Throws<ArgumentException>(
            () => registry.Register(" ", ParticipantSubject(Guid.NewGuid(), Guid.NewGuid()), () => { }));
        Assert.Throws<ArgumentNullException>(
            () => registry.Register("conn", ParticipantSubject(Guid.NewGuid(), Guid.NewGuid()), null!));
    }

    // --- Cross-instance propagation (CORE-RES-008) -------------------------------

    [Fact]
    public async Task Evicting_a_participant_broadcasts_the_participant_descriptor()
    {
        var backplane = new FakeEvictionBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);
        var participantId = Guid.NewGuid();

        await registry.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        // The local eviction is broadcast so a socket the same participant holds on another replica is aborted too.
        Assert.Equal(
            new[] { RealtimeConnectionEviction.ForParticipant(_org, _workspace, _session, participantId).Serialize() },
            backplane.Published);
    }

    [Fact]
    public async Task Evicting_a_member_broadcasts_the_member_descriptor()
    {
        var backplane = new FakeEvictionBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);
        var userProfileId = Guid.NewGuid();

        await registry.EvictWorkspaceMemberAsync(_org, _workspace, userProfileId, CancellationToken.None);

        Assert.Equal(
            new[] { RealtimeConnectionEviction.ForWorkspaceMember(_org, _workspace, userProfileId).Serialize() },
            backplane.Published);
    }

    [Fact]
    public async Task Evicting_an_empty_id_broadcasts_nothing()
    {
        var backplane = new FakeEvictionBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);

        // An empty id can never address a real connection on any replica, so it is neither evicted nor broadcast.
        await registry.EvictParticipantAsync(_org, _workspace, _session, Guid.Empty, CancellationToken.None);
        await registry.EvictWorkspaceMemberAsync(_org, _workspace, Guid.Empty, CancellationToken.None);

        Assert.Empty(backplane.Published);
    }

    [Fact]
    public async Task A_demotion_on_one_instance_aborts_the_target_socket_on_a_second_instance()
    {
        // Two API replicas modeled as two registries sharing one backplane (an in-memory fake standing in for the
        // Valkey/Redis pub/sub server). The demoted host's socket is held by instance B only.
        var backplane = new FakeEvictionBackplane();
        var instanceA = new RealtimeConnectionRegistry(backplane);
        var instanceB = new RealtimeConnectionRegistry(backplane);

        // Each replica's listener applies a peer's eviction to its OWN registry (the production wiring).
        backplane.Subscribe(instanceA.ApplyRemoteEviction);
        backplane.Subscribe(instanceB.ApplyRemoteEviction);

        var demoted = Guid.NewGuid();
        var target = Track(instanceB, "conn-host", MemberSubject(demoted));
        // Precision bystander on B: the demoted subject's OWN participant connection is unchanged by a role change.
        var bystander = Track(instanceB, "conn-participant", ParticipantSubject(Guid.NewGuid(), demoted));

        // The demotion is handled on instance A, which holds no matching socket; the broadcast crosses to B.
        await instanceA.EvictWorkspaceMemberAsync(_org, _workspace, demoted, CancellationToken.None);

        Assert.True(target.Aborted);
        Assert.False(bystander.Aborted);
    }

    [Fact]
    public async Task A_participant_removal_on_one_instance_aborts_the_target_socket_on_a_second_instance()
    {
        var backplane = new FakeEvictionBackplane();
        var instanceA = new RealtimeConnectionRegistry(backplane);
        var instanceB = new RealtimeConnectionRegistry(backplane);
        backplane.Subscribe(instanceA.ApplyRemoteEviction);
        backplane.Subscribe(instanceB.ApplyRemoteEviction);

        var participantId = Guid.NewGuid();
        var target = Track(instanceB, "conn-target", ParticipantSubject(participantId, Guid.NewGuid()));
        var bystander = Track(instanceB, "conn-other", ParticipantSubject(Guid.NewGuid(), Guid.NewGuid()));

        await instanceA.EvictParticipantAsync(_org, _workspace, _session, participantId, CancellationToken.None);

        Assert.True(target.Aborted);
        Assert.False(bystander.Aborted);
    }

    [Fact]
    public void A_received_eviction_is_applied_locally_and_never_rebroadcast()
    {
        var backplane = new FakeEvictionBackplane();
        var registry = new RealtimeConnectionRegistry(backplane);
        backplane.Subscribe(registry.ApplyRemoteEviction);

        registry.ApplyRemoteEviction(
            RealtimeConnectionEviction.ForWorkspaceMember(_org, _workspace, Guid.NewGuid()).Serialize());

        // Applying a peer's eviction must NOT publish anything, or it would echo across the backplane forever.
        Assert.Empty(backplane.Published);
    }

    [Fact]
    public void A_received_participant_eviction_aborts_the_matching_local_socket()
    {
        var registry = new RealtimeConnectionRegistry();
        var participantId = Guid.NewGuid();
        var target = Track(registry, "conn-target", ParticipantSubject(participantId, Guid.NewGuid()));
        var bystander = Track(registry, "conn-other", ParticipantSubject(Guid.NewGuid(), Guid.NewGuid()));

        registry.ApplyRemoteEviction(
            RealtimeConnectionEviction.ForParticipant(_org, _workspace, _session, participantId).Serialize());

        Assert.True(target.Aborted);
        Assert.False(bystander.Aborted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("x:00000000000000000000000000000000")] // unknown kind prefix
    [InlineData("m:00000000000000000000000000000000")] // member token missing segments
    public void A_malformed_remote_eviction_aborts_nothing(string? token)
    {
        var registry = new RealtimeConnectionRegistry();
        var connection = Track(registry, "conn", MemberSubject(Guid.NewGuid()));

        registry.ApplyRemoteEviction(token!);

        // A malformed/unrecognized token must never abort an unintended connection.
        Assert.False(connection.Aborted);
    }

    [Theory]
    [InlineData(true, false)] // foreign tenant
    [InlineData(false, true)] // foreign workspace
    public void A_received_member_eviction_for_a_foreign_scope_aborts_nothing(bool foreignOrg, bool foreignWorkspace)
    {
        var registry = new RealtimeConnectionRegistry();
        var userProfileId = Guid.NewGuid();
        var connection = Track(registry, "conn", MemberSubject(userProfileId));

        // The descriptor carries the scope, so a token for another tenant/workspace aborts nothing (threats T1/T5).
        registry.ApplyRemoteEviction(
            RealtimeConnectionEviction.ForWorkspaceMember(
                foreignOrg ? Guid.NewGuid() : _org,
                foreignWorkspace ? Guid.NewGuid() : _workspace,
                userProfileId).Serialize());

        Assert.False(connection.Aborted);
    }

    // --- Helpers -----------------------------------------------------------------

    private static RealtimeConnectionSubject ParticipantSubject(Guid participantId, Guid userProfileId)
        => new(_org, _workspace, _session, userProfileId, ParticipantId: participantId);

    private static RealtimeConnectionSubject MemberSubject(Guid userProfileId)
        => new(_org, _workspace, _session, userProfileId, ParticipantId: null);

    private static AbortProbe Track(RealtimeConnectionRegistry registry, string connectionId, RealtimeConnectionSubject subject)
    {
        var probe = new AbortProbe();
        registry.Register(connectionId, subject, probe.Abort);
        return probe;
    }

    private static AbortProbe TrackFaulting(RealtimeConnectionRegistry registry, string connectionId, RealtimeConnectionSubject subject)
    {
        var probe = new AbortProbe(throwOnAbort: true);
        registry.Register(connectionId, subject, probe.Abort);
        return probe;
    }

    /// <summary>
    /// An in-memory stand-in for the Valkey/Redis pub/sub server shared by the modeled replicas (CORE-RES-008): a
    /// publish records the descriptor and fans it out to every subscribed handler (as Redis delivers a published
    /// message to every subscriber, including the publisher's own instance).
    /// </summary>
    private sealed class FakeEvictionBackplane : IRealtimeConnectionEvictionBackplane
    {
        private readonly List<Action<string>> _handlers = [];

        public List<string> Published { get; } = [];

        public void Publish(string evictionToken)
        {
            Published.Add(evictionToken);
            foreach (var handler in _handlers.ToArray())
            {
                handler(evictionToken);
            }
        }

        public void Subscribe(Action<string> onRemoteEviction) => _handlers.Add(onRemoteEviction);
    }

    /// <summary>A stand-in for a connection's abort handle that records whether (and how often) it was invoked.</summary>
    private sealed class AbortProbe(bool throwOnAbort = false)
    {
        public int AbortCount { get; private set; }

        public bool Aborted => AbortCount > 0;

        public bool AbortAttempted { get; private set; }

        public void Abort()
        {
            AbortAttempted = true;
            if (throwOnAbort)
            {
                throw new InvalidOperationException("The connection was already torn down.");
            }

            AbortCount++;
        }
    }
}
