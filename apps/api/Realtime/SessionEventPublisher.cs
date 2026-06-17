// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
///
/// The append and the delivery are exposed as separate steps (<see cref="AppendAsync"/> /
/// <see cref="DeliverAsync"/>) as well as the combined <see cref="PublishAsync"/>, so a multi-step command
/// can append the durable event INSIDE its unit-of-work transaction and deliver it AFTER the commit
/// (commit-then-publish, CORE-CONC-002) — keeping the append atomic with the rest of the command while a
/// delivery failure cannot roll back committed state.
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

    // The distributed-tracing source (CORE-OBS-003): the "publish" half of the reveal/publish path produces a
    // span here, nested under the reveal span. Optional for the SAME reason as the log context — DI injects
    // the singleton in the running host, and it is left null in tests; when absent (or when nothing is
    // listening) span creation is a no-op and behavior is unchanged.
    private readonly LiveCoreActivitySource? _activitySource;

    public SessionEventPublisher(
        ISessionEventRepository events,
        IRealtimeBackplane backplane,
        ISessionEventRecipientResolver recipients,
        LiveCoreMetrics metrics,
        RequestLogContext? requestLogContext = null,
        LiveCoreActivitySource? activitySource = null)
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
        _activitySource = activitySource;
    }

    /// <inheritdoc />
    public async Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // The combined append-then-deliver, for callers that do not separate the two across a transaction
        // boundary (the participant join/leave services). The append is the source of truth and runs first,
        // exactly as before; delivery is best-effort over the realtime transport.
        await AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        await DeliverAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // Trace the publish (CORE-OBS-003). The span is tagged with the stable event-type name only — never
        // the payload (threat T7) — and nests under the reveal/request span via Activity.Current. A no-op
        // when no source is injected or nothing is listening, so the best-effort delivery is unaffected.
        using var activity = _activitySource?.StartSessionEventPublish(sessionEvent.EventType);

        // Persist the durable event (the source of truth for replay). For the reveal/hide and session
        // start/end/cancel commands this runs INSIDE the handler's unit-of-work transaction (CORE-CONC-002),
        // so the event is committed ATOMICALLY with the rule/state change, audit and quota change it
        // accompanies — a part-way failure rolls them all back together and the append-only stream can never
        // diverge from persisted state.
        await _events.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // Enrich the per-request log context with the published event's id (CORE-OBS-002), so this request's
        // remaining log lines carry event_id. The id is an opaque surrogate, never the event payload (threat
        // T7 in docs/07_SECURITY_THREAT_MODEL.md).
        _requestLogContext?.SetEventId(sessionEvent.Id);
    }

    /// <inheritdoc />
    public async Task DeliverAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // Compute the per-recipient deliveries (groups + projected envelopes) and forward each over the
        // scale-out backplane. The resolver omits any recipient that may not see the event's subject, so the
        // send never leaks a hidden event (threat T3); the backplane only forwards what it is given. For the
        // transactional commands this runs AFTER the unit-of-work transaction commits (commit-then-publish,
        // CORE-CONC-002), so a delivery failure can never roll back already-committed state — the durable
        // event is the source of truth and a reconnecting client replays a missed push later (CORE-RT-005).
        var deliveries = await _recipients.ResolveAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        foreach (var delivery in deliveries)
        {
            try
            {
                await _backplane
                    .SendToGroupAsync(delivery.Group, SessionEventEnvelope.ClientMethod, delivery.Envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // GENUINELY BEST-EFFORT (CORE-RES-001): a backplane (Redis/Valkey) transport failure is RECORDED
                // and SWALLOWED, never rethrown. The durable event already committed (it is the source of truth),
                // so a committed reveal/hide/session/participant operation must still return success during a
                // backplane outage — counting-and-dropping the live push, not failing the request with a 500. A
                // reconnecting client replays the missed push from the durable stream later (CORE-RT-005). The
                // failure is counted on the docs/15_OBSERVABILITY.md "event delivery failures" signal
                // (CORE-OBS-001); the metric counts only — no event content is ever attached to it (threat T7).
                // Swallowing is PER DELIVERY, so one recipient group's transport hiccup never suppresses the
                // deliveries to the others. A genuine cancellation is NOT a transport failure and is left to
                // propagate (the request is being torn down anyway).
                _metrics.RecordEventDeliveryFailure();
            }
        }
    }
}
