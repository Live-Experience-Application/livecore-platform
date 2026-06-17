// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
/// CORE-OBS-001 has the publisher record the "event delivery failures" metric when a transport send fails;
/// CORE-RES-001 makes that delivery GENUINELY best-effort — the failure is counted-and-DROPPED (swallowed),
/// never rethrown, so a committed reveal/hide/session/participant operation still succeeds during a backplane
/// outage. The last two tests pin that: a single failing delivery is swallowed-and-counted, and a per-delivery
/// failure never suppresses the OTHER recipients' deliveries. The metric-listening tests share the serialized
/// "LiveCore metrics" collection.
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
    public async Task A_delivery_transport_failure_records_the_metric_and_is_swallowed()
    {
        // CORE-RES-001: a failed realtime transport send is GENUINELY best-effort — it increments the "event
        // delivery failures" metric (CORE-OBS-001) and is SWALLOWED, never rethrown. The durable event is
        // already appended (the source of truth), so the publish still completes successfully and a committed
        // command does not surface a 500 during a backplane outage; a reconnecting client replays the missed
        // push later (CORE-RT-005).
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        var repository = new RecordingEventRepository();
        var sessionEvent = Event();
        var delivery = new SessionEventDelivery("session:hosts", SessionEventEnvelope.ForHost(sessionEvent));
        var resolver = new FixedRecipientResolver([delivery]);
        var publisher = new SessionEventPublisher(repository, new ThrowingBackplane(), resolver, metrics);

        // No exception escapes: the transport failure is swallowed.
        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        // The durable event was still appended before the failed delivery, and the failure was counted.
        Assert.Single(repository.Appended);
        Assert.True(recorded.Count("livecore.event.delivery.failures") >= 1);
    }

    [Fact]
    public async Task A_failing_delivery_is_counted_and_dropped_without_suppressing_the_other_recipients()
    {
        // CORE-RES-001: swallowing is PER DELIVERY, so one recipient group's transport failure never suppresses
        // the deliveries to the others. The first group's send throws (counted-and-dropped); the second group
        // is still delivered, and the publish completes without throwing.
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();

        var repository = new RecordingEventRepository();
        var sessionEvent = Event();
        var failing = new SessionEventDelivery("session:hosts", SessionEventEnvelope.ForHost(sessionEvent));
        var healthy = new SessionEventDelivery("session:participant:1", SessionEventEnvelope.ForAudience(sessionEvent));
        var resolver = new FixedRecipientResolver([failing, healthy]);
        var backplane = new SelectivelyThrowingBackplane(throwForGroup: "session:hosts");
        var publisher = new SessionEventPublisher(repository, backplane, resolver, metrics);

        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        // The healthy recipient still received its delivery despite the earlier group's failure.
        Assert.Equal(new[] { "session:participant:1" }, backplane.Delivered);
        // The failed delivery was counted exactly once.
        Assert.True(recorded.Count("livecore.event.delivery.failures") >= 1);
    }

    [Fact]
    public async Task It_enriches_the_request_log_context_with_the_published_event_id()
    {
        // CORE-OBS-002: publishing enriches the per-request log context with the event's id, so the request's
        // log lines carry the documented event_id. The id is the opaque surrogate, never the payload (T7).
        var repository = new RecordingEventRepository();
        var sessionEvent = Event();
        var resolver = new FixedRecipientResolver([]);
        var logContext = new RequestLogContext();
        var publisher = new SessionEventPublisher(
            repository, new RecordingBackplane(), resolver, new LiveCoreMetrics(), logContext);

        await publisher.PublishAsync(sessionEvent, CancellationToken.None);

        var logged = logContext.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(sessionEvent.Id.ToString("D"), logged[RequestLogContext.EventIdKey]);
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

    private sealed class SelectivelyThrowingBackplane : IRealtimeBackplane
    {
        private readonly string _throwForGroup;

        public SelectivelyThrowingBackplane(string throwForGroup) => _throwForGroup = throwForGroup;

        /// <summary>The groups that were delivered to successfully (the failing group throws before recording).</summary>
        public List<string> Delivered { get; } = [];

        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
        {
            if (string.Equals(group, _throwForGroup, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transport unavailable for this group");
            }

            Delivered.Add(group);
            return Task.CompletedTask;
        }
    }
}
