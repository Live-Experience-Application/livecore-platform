// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Observability;
using LiveCore.Api.Realtime;
using LiveCore.Api.UnitTests.Observability;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Tracing tests for <see cref="SessionEventPublisher"/> (CORE-OBS-003): publishing a session event produces a
/// "publish" span through <see cref="LiveCoreActivitySource"/> — the realtime half of the reveal/publish path
/// — tagged with the stable event-type name and nothing sensitive (threat T7). They use a real
/// <see cref="System.Diagnostics.ActivityListener"/> (<see cref="RecordedActivities"/>) and the same in-memory
/// test doubles the publisher's behavioral tests use, so the span is observed deterministically without a live
/// connection or an exporter.
///
/// The publisher takes the source as an OPTIONAL dependency (DI injects the singleton in the running host, it
/// is null in the behavioral tests), so injecting it here only ADDS the span and changes no delivery behavior.
/// The class shares the serialized "LiveCore tracing" collection so the listener never captures another
/// span-producing test's activities.
/// </summary>
[Collection(LiveCoreTracingTestCollection.Name)]
public sealed class SessionEventPublisherTracingTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static SessionEvent Event()
        => SessionEvent.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionEventTypes.ContentRevealed,
            Guid.NewGuid(), targetParticipantId: null, "{\"resourceId\":\"x\"}", 1, _now,
            visibilitySubjectType: "Entity", visibilitySubjectId: Guid.NewGuid());

    [Fact]
    public async Task Publishing_produces_a_publish_span_tagged_by_event_type()
    {
        using var source = new LiveCoreActivitySource();
        using var recorded = new RecordedActivities();

        var publisher = new SessionEventPublisher(
            new RecordingEventRepository(),
            new RecordingBackplane(),
            new FixedRecipientResolver(),
            new LiveCoreMetrics(),
            requestLogContext: null,
            activitySource: source);

        await publisher.PublishAsync(Event(), CancellationToken.None);

        Assert.Equal(1, recorded.Count("livecore.session_event.publish"));
        Assert.True(recorded.HasTag(
            "livecore.session_event.publish", "livecore.event.type", SessionEventTypes.ContentRevealed));
    }

    [Fact]
    public async Task Publishing_without_a_source_produces_no_span_and_still_delivers()
    {
        // With no source injected (the behavioral default) publishing produces no span and is unchanged — the
        // event is still appended and delivered.
        using var recorded = new RecordedActivities();

        var repository = new RecordingEventRepository();
        var publisher = new SessionEventPublisher(
            repository, new RecordingBackplane(), new FixedRecipientResolver(), new LiveCoreMetrics());

        await publisher.PublishAsync(Event(), CancellationToken.None);

        Assert.Equal(0, recorded.Count("livecore.session_event.publish"));
        Assert.Single(repository.Appended);
    }

    private sealed class FixedRecipientResolver : ISessionEventRecipientResolver
    {
        public Task<IReadOnlyList<SessionEventDelivery>> ResolveAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionEventDelivery>>([]);
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
        public Task SendToGroupAsync(string group, string method, object payload, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
