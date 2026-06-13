using LiveCore.Api.Observability;
using LiveCore.Api.Realtime;
using LiveCore.Api.UnitTests.Observability;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tests for <see cref="SessionEventPublisher"/> (CORE-RT-003 append + deliver; CORE-RT-004 delegates the
/// recipient computation to <see cref="ISessionEventRecipientResolver"/>; CORE-RT-006 forwards each
/// computed delivery over the <see cref="IRealtimeBackplane"/> scale-out seam). They use a fake append-only
/// repository (recording the appended event), a fake recipient resolver (returning a fixed delivery plan)
/// and a recording backplane (recording which GROUP each send targeted, on which client method, with which
/// payload), so the test observes exactly the "persist event -> compute recipients -> send to recipient
/// groups" flow (docs/11_REALTIME_SYNC.md) deterministically, without a live connection.
///
/// The publisher is deliberately THIN: WHO receives an event and WHICH projection they get is the
/// resolver's job (covered by <see cref="SessionEventRecipientResolverTests"/>); the publisher only
/// appends once and then forwards each computed delivery to the backplane unchanged. These tests pin
/// exactly that. All fixtures are generic (AGENTS.md).
///
/// CORE-OBS-001 additionally has the publisher record the "event delivery failures" metric when a transport
/// send fails, WITHOUT changing the best-effort behavior (the failure still propagates); the last test pins
/// that. The metric-listening tests share the serialized "LiveCore metrics" collection.
/// </summary>
[Collection(LiveCoreMetricsTestCollection.Name)]
public sealed class SessionEventPublisherTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent Event()
        => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed,
            Guid.NewGuid(), targetParticipantId: null, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());

    [Fact]
    public async Task It_appends_the_event_once_then_forwards_each_resolved_delivery()
    {
        var repository = new RecordingEventRepository();
        var backplane = new RecordingBackplane();
        var sessionEvent = Event();
        var hosts = new SessionEventDelivery("session:hosts", SessionEventEnvelope.ForHost(sessionEvent));
        var audience = new SessionEventDelivery("session:participant:1", SessionEventEnvelope.ForAudience(sessionEvent));
        var resolver = new FixedRecipientResolver([hosts, audience]);
        var publisher = new SessionEventPublisher(repository, backplane, resolver, new LiveCoreMetrics());

        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        // Persisted exactly once, BEFORE any delivery is computed.
        Assert.Single(repository.Appended);
        Assert.Equal(sessionEvent.Id, repository.Appended[0].Id);
        Assert.Same(sessionEvent, resolver.Resolved);

        // Forwarded to exactly the resolver's groups, in order, each on the SessionEvent client method
        // with the envelope the resolver chose for that group — the backplane receives them verbatim.
        Assert.Equal(
            new[] { "session:hosts", "session:participant:1" },
            backplane.Sends.Select(send => send.Group));
        Assert.All(backplane.Sends, send => Assert.Equal(SessionEventEnvelope.ClientMethod, send.Method));
        Assert.Same(hosts.Envelope, backplane.Sends[0].Payload);
        Assert.Same(audience.Envelope, backplane.Sends[1].Payload);
    }

    [Fact]
    public async Task It_appends_before_resolving_recipients()
    {
        // The durable append is the source of truth and must happen before delivery is even computed, so a
        // reconnecting client can replay an event whose live push failed (CORE-RT-005).
        var repository = new RecordingEventRepository();
        var resolver = new FixedRecipientResolver([]) { AppendCountSource = () => repository.Appended.Count };
        var publisher = new SessionEventPublisher(repository, new RecordingBackplane(), resolver, new LiveCoreMetrics());

        await publisher.PublishAsync(Event(), CancellationToken.None);

        // When the resolver ran, the event had already been appended.
        Assert.Equal(1, resolver.AppendCountAtResolve);
    }

    [Fact]
    public async Task A_delivery_transport_failure_records_the_metric_and_still_propagates()
    {
        // CORE-OBS-001: a failed realtime transport send increments the "event delivery failures" metric, but
        // the best-effort behavior is unchanged — the failure still propagates (the durable event is already
        // appended, so a reconnecting client replays it later, CORE-RT-005).
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        var repository = new RecordingEventRepository();
        var sessionEvent = Event();
        var delivery = new SessionEventDelivery("session:hosts", SessionEventEnvelope.ForHost(sessionEvent));
        var resolver = new FixedRecipientResolver([delivery]);
        var publisher = new SessionEventPublisher(repository, new ThrowingBackplane(), resolver, metrics);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.PublishAsync(sessionEvent, CancellationToken.None));

        // The durable event was still appended before the failed delivery, and the failure was counted.
        Assert.Single(repository.Appended);
        Assert.True(recorded.Count("livecore.event.delivery.failures") >= 1);
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

    private sealed class RecordingBackplane : IRealtimeBackplane
    {
        public List<(string Group, string Method, object Payload)> Sends { get; } = [];

        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
        {
            Sends.Add((group, method, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingBackplane : IRealtimeBackplane
    {
        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
            => throw new InvalidOperationException("transport unavailable");
    }
}
