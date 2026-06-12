using LiveCore.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventPublisher"/> (CORE-RT-003) — append + deliver. They use a fake
/// append-only repository (recording the appended event) and a recording <see cref="IHubContext{SessionHub}"/>
/// (recording which GROUP each send targeted), so the test observes exactly the "persist event ->
/// compute recipients -> send to recipient groups" flow (docs/11_REALTIME_SYNC.md) deterministically,
/// without a live connection.
///
/// The security-critical property (the epic's "selected/unselected participants" requirement) is the
/// recipient-group computation: a SELECTED-participant event is delivered to that ONE participant's group
/// (plus the hosts group), so — combined with CORE-RT-002, where a participant joins ONLY its own group —
/// a non-selected participant cannot receive it (threat T3). All group NAMES come from
/// <see cref="RealtimeGroups"/>. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventPublisherTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent Event(Guid session, Guid? target)
        => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), session, SessionEventTypes.ContentRevealed,
            Guid.NewGuid(), target, "{\"resourceId\":\"x\"}", 1, _now);

    [Fact]
    public async Task A_selected_event_is_delivered_to_the_participant_and_hosts_groups_only()
    {
        var session = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var repository = new RecordingEventRepository();
        var hub = new RecordingHubContext();
        var publisher = new SessionEventPublisher(repository, hub);
        var sessionEvent = Event(session, participant);

        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        // Appended exactly once.
        Assert.Single(repository.Appended);
        Assert.Equal(sessionEvent.Id, repository.Appended[0].Id);

        // Delivered to exactly the selected participant's group and the session hosts group — NOT to
        // the observers group, and NOT to any other participant's group.
        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionParticipant(session, participant),
                RealtimeGroups.SessionHosts(session),
            },
            hub.Clients.GroupSends.Select(send => send.Group));

        // The envelope is the recipient-safe projection, delivered on the SessionEvent client method.
        var (_, method, payload) = hub.Clients.GroupSends[0];
        Assert.Equal(SessionEventEnvelope.ClientMethod, method);
        var envelope = Assert.IsType<SessionEventEnvelope>(payload);
        Assert.Equal(sessionEvent.Id, envelope.EventId);
        Assert.Equal(session, envelope.SessionId);
    }

    [Fact]
    public async Task An_audience_wide_event_is_delivered_to_the_hosts_and_observers_groups()
    {
        var session = Guid.NewGuid();
        var repository = new RecordingEventRepository();
        var hub = new RecordingHubContext();
        var publisher = new SessionEventPublisher(repository, hub);

        await publisher.PublishAsync(Event(session, target: null), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                RealtimeGroups.SessionHosts(session),
                RealtimeGroups.SessionObservers(session),
            },
            hub.Clients.GroupSends.Select(send => send.Group));
    }

    [Fact]
    public async Task A_non_selected_participant_group_is_never_targeted_by_a_selected_event()
    {
        var session = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var hub = new RecordingHubContext();
        var publisher = new SessionEventPublisher(new RecordingEventRepository(), hub);

        await publisher.PublishAsync(Event(session, selected), CancellationToken.None);

        // The other participant's group is never among the delivery targets (crown jewel, threat T3).
        Assert.DoesNotContain(RealtimeGroups.SessionParticipant(session, other), hub.Clients.GroupSends.Select(s => s.Group));
    }

    // --- Test doubles ----------------------------------------------------------

    private sealed class RecordingEventRepository : ISessionEventRepository
    {
        public List<SessionEvent> Appended { get; } = [];

        public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
        {
            Appended.Add(sessionEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
            Guid organizationId,
            Guid sessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHubContext : IHubContext<SessionHub>
    {
        public RecordingHubClients Clients { get; } = new();

        IHubClients IHubContext<SessionHub>.Clients => Clients;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public List<(string Group, string Method, object? Payload)> GroupSends { get; } = [];

        public IClientProxy Group(string groupName) => new GroupRecorder(groupName, GroupSends);

        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
            => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class GroupRecorder : IClientProxy
    {
        private readonly string _group;
        private readonly List<(string Group, string Method, object? Payload)> _sends;

        public GroupRecorder(string group, List<(string Group, string Method, object? Payload)> sends)
        {
            _group = group;
            _sends = sends;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _sends.Add((_group, method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }
}
