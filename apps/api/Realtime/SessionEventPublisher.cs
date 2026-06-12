using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Appends a session event and delivers it to its recipients (CORE-RT-003, <see cref="ISessionEventPublisher"/>;
/// recipient-specific projection added in CORE-RT-004). It realizes the documented flow "persist event ->
/// compute recipients -> project payload -> send to recipient groups" (docs/11_REALTIME_SYNC.md): first
/// the event is appended to the durable, append-only stream (<see cref="ISessionEventRepository"/>) — the
/// source of truth that reconnect replay (CORE-RT-005) reconstructs from — and only then are the
/// per-recipient deliveries computed (<see cref="ISessionEventRecipientResolver"/>) and sent over SignalR.
///
/// The publisher itself is deliberately THIN: WHO receives the event, WHICH server-managed group they are
/// in, and WHICH projection they get (the host projection vs the recipient-safe audience projection, and
/// only for recipients allowed to see the event's visibility subject) are all decided by the recipient
/// resolver, so the anti-leak logic lives in one tested place ("Events are never broadcast blindly",
/// docs/11_REALTIME_SYNC.md; threat T3). The publisher just appends, then sends each computed delivery to
/// its group.
///
/// Delivery is best-effort: the append already committed the durable event, so a transient transport
/// failure loses only the live push, not the recorded fact (a reconnecting client replays it later,
/// CORE-RT-005).
/// </summary>
internal sealed class SessionEventPublisher : ISessionEventPublisher
{
    private readonly ISessionEventRepository _events;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ISessionEventRecipientResolver _recipients;

    public SessionEventPublisher(
        ISessionEventRepository events,
        IHubContext<SessionHub> hub,
        ISessionEventRecipientResolver recipients)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(recipients);
        _events = events;
        _hub = hub;
        _recipients = recipients;
    }

    /// <inheritdoc />
    public async Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // 1. Persist the durable event first (the source of truth for replay).
        await _events.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // 2. Compute the per-recipient deliveries (groups + projected envelopes) and send each. The
        //    resolver omits any recipient that may not see the event's subject, so the send never leaks a
        //    hidden event (threat T3).
        var deliveries = await _recipients.ResolveAsync(sessionEvent, cancellationToken).ConfigureAwait(false);
        foreach (var delivery in deliveries)
        {
            await _hub.Clients
                .Group(delivery.Group)
                .SendAsync(SessionEventEnvelope.ClientMethod, delivery.Envelope, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
