using LiveCore.Api.Observability;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Appends a session event and delivers it to its recipients (CORE-RT-003, <see cref="ISessionEventPublisher"/>;
/// recipient-specific projection added in CORE-RT-004; scale-out seam in CORE-RT-006). It realizes the
/// documented flow "persist event -> compute recipients -> project payload -> send to recipient groups"
/// (docs/11_REALTIME_SYNC.md): first the event is appended to the durable, append-only stream
/// (<see cref="ISessionEventRepository"/>) — the source of truth that reconnect replay (CORE-RT-005)
/// reconstructs from — and only then are the per-recipient deliveries computed
/// (<see cref="ISessionEventRecipientResolver"/>) and sent.
///
/// The publisher itself is deliberately THIN: WHO receives the event, WHICH server-managed group they are
/// in, and WHICH projection they get (the host projection vs the recipient-safe audience projection, and
/// only for recipients allowed to see the event's visibility subject) are all decided by the recipient
/// resolver, so the anti-leak logic lives in one tested place ("Events are never broadcast blindly",
/// docs/11_REALTIME_SYNC.md; threat T3). The publisher just appends, then hands each computed delivery to
/// the <see cref="IRealtimeBackplane"/> (CORE-RT-006). The backplane is the single transport boundary — the
/// in-process default sends to this instance's connections, and a multi-instance deployment swaps in a
/// Valkey/Redis-compatible one (docs/11_REALTIME_SYNC.md "Scale-out") — but it only ever forwards an
/// already-authorized delivery, so the recipient resolver stays the single send path and the swap cannot
/// leak a hidden event.
///
/// Delivery is best-effort: the append already committed the durable event, so a transient transport
/// failure loses only the live push, not the recorded fact (a reconnecting client replays it later,
/// CORE-RT-005).
/// </summary>
internal sealed class SessionEventPublisher : ISessionEventPublisher
{
    private readonly ISessionEventRepository _events;
    private readonly IRealtimeBackplane _backplane;
    private readonly ISessionEventRecipientResolver _recipients;
    private readonly LiveCoreMetrics _metrics;

    // The per-request structured log context (CORE-OBS-002), enriched with the published event's id so the
    // request's log lines carry event_id (docs/15_OBSERVABILITY.md "event_id when applicable"). Optional so
    // the publisher stays unit-testable: DI injects the request-scoped instance the log-scope middleware
    // opened in the running host, and it is left null in tests.
    private readonly RequestLogContext? _requestLogContext;

    public SessionEventPublisher(
        ISessionEventRepository events,
        IRealtimeBackplane backplane,
        ISessionEventRecipientResolver recipients,
        LiveCoreMetrics metrics,
        RequestLogContext? requestLogContext = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(metrics);
        _events = events;
        _backplane = backplane;
        _recipients = recipients;
        _metrics = metrics;
        _requestLogContext = requestLogContext;
    }

    /// <inheritdoc />
    public async Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // 1. Persist the durable event first (the source of truth for replay).
        await _events.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // Enrich the per-request log context with the published event's id (CORE-OBS-002), so this request's
        // remaining log lines carry event_id. The id is an opaque surrogate, never the event payload (threat
        // T7 in docs/07_SECURITY_THREAT_MODEL.md).
        _requestLogContext?.SetEventId(sessionEvent.Id);

        // 2. Compute the per-recipient deliveries (groups + projected envelopes) and forward each over the
        //    scale-out backplane. The resolver omits any recipient that may not see the event's subject, so
        //    the send never leaks a hidden event (threat T3); the backplane only forwards what it is given.
        var deliveries = await _recipients.ResolveAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        foreach (var delivery in deliveries)
        {
            try
            {
                await _backplane
                    .SendToGroupAsync(delivery.Group, SessionEventEnvelope.ClientMethod, delivery.Envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Record the docs/15_OBSERVABILITY.md "event delivery failures" signal (CORE-OBS-001), then
                // rethrow unchanged: the durable event is already persisted, so behavior is unaltered and a
                // reconnecting client replays it later (CORE-RT-005). Counting only — no event content is
                // ever attached to the metric (threat T7).
                _metrics.RecordEventDeliveryFailure();
                throw;
            }
        }
    }
}
