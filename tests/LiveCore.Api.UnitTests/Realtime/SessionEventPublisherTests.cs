using LiveCore.Api.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventPublisher"/> (CORE-RT-003 append + deliver; CORE-RT-004 delegates the
/// recipient computation to <see cref="ISessionEventRecipientResolver"/>). They use a fake append-only
/// repository (recording the appended event), a fake recipient resolver (returning a fixed delivery plan)
/// and a recording <see cref="IHubContext{SessionHub}"/> (recording which GROUP each send targeted and the
/// payload), so the test observes exactly the "persist event -> compute recipients -> send to recipient
/// groups" flow (docs/11_REALTIME_SYNC.md) deterministically, without a live connection.
///
/// The publisher is deliberately THIN: WHO receives an event and WHICH projection they get is the
/// resolver's job (covered by <see cref="SessionEventRecipientResolverTests"/>); the publisher only
/// appends once and then sends each computed delivery to its group. These tests pin exactly that. All
/// fixtures are generic (AGENTS.md).
/// </summary>
public sealed class SessionEventPublisherTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent Event()
        => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed,
            Guid.NewGuid(), targetParticipantId: null, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());

    [Fact]
    public async Task It_appends_the_event_once_then_sends_each_resolved_delivery()
    {
        var repository = new RecordingEventRepository();
        var hub = new RecordingHubContext();
        var sessionEvent = Event();
        var hosts = new SessionEventDelivery("session:hosts", SessionEventEnvelope.ForHost(sessionEvent));
        var audience = new SessionEventDelivery("session:participant:1", SessionEventEnvelope.ForAudience(sessionEvent));
        var resolver = new FixedRecipientResolver([hosts, audience]);
        var publisher = new SessionEventPublisher(repository, hub, resolver);

        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        // Persisted exactly once, BEFORE any delivery is computed.
        Assert.Single(repository.Appended);
        Assert.Equal(sessionEvent.Id, repository.Appended[0].Id);
        Assert.Same(sessionEvent, resolver.Resolved);

        // Delivered to exactly the resolver's groups, in order, each on the SessionEvent client method
        // with the envelope the resolver chose for that group.
        Assert.Equal(
            new[] { "session:hosts", "session:participant:1" },
            hub.Clients.GroupSends.Select(send => send.Group));
        Assert.All(hub.Clients.GroupSends, send => Assert.Equal(SessionEventEnvelope.ClientMethod, send.Method));
        Assert.Same(hosts.Envelope, hub.Clients.GroupSends[0].Payload);
        Assert.Same(audience.Envelope, hub.Clients.GroupSends[1].Payload);
    }

    [Fact]
    public async Task It_appends_before_resolving_recipients()
    {
        // The durable append is the source of truth and must happen before delivery is even computed, so a
        // reconnecting client can replay an event whose live push failed (CORE-RT-005).
        var repository = new RecordingEventRepository();
        var resolver = new FixedRecipientResolver([]) { AppendCountSource = () => repository.Appended.Count };
        var publisher = new SessionEventPublisher(repository, new RecordingHubContext(), resolver);

        await publisher.PublishAsync(Event(), CancellationToken.None);

        // When the resolver ran, the event had already been appended.
        Assert.Equal(1, resolver.AppendCountAtResolve);
    }

    // --- Test doubles ----------------------------------------------------------

    private sealed class FixedRecipientResolver : ISessionEventRecipientResolver
    {
        private readonly IReadOnlyList<SessionEventDelivery> _deliveries;

        public FixedRecipientResolver(IReadOnlyList<SessionEventDelivery> deliveries) => _deliveries = deliveries;

        public SessionEvent? Resolved { get; private set; }

        public Func<int>? AppendCountSource { get; set; }

        public int AppendCountAtResolve { get; private set; }

        public Task<IReadOnlyList<SessionEventDelivery>> ResolveAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken)
        {
            Resolved = sessionEvent;
            AppendCountAtResolve = AppendCountSource?.Invoke() ?? 0;
            return Task.FromResult(_deliveries);
        }
    }

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
