using Microsoft.AspNetCore.SignalR;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Appends a session event and delivers it to its recipient groups (CORE-RT-003,
/// <see cref="ISessionEventPublisher"/>). It realizes the documented flow "persist event -> compute
/// recipients -> send to recipient groups" (docs/11_REALTIME_SYNC.md): first the event is appended to the
/// durable, append-only stream (<see cref="ISessionEventRepository"/>) — the source of truth that
/// reconnect replay (CORE-RT-005) reconstructs from — and only then is the recipient-safe envelope sent
/// over SignalR to the SERVER-COMPUTED groups (<see cref="RealtimeGroups"/>), never to client-chosen
/// names.
///
/// RECIPIENT GROUPS (the "compute recipients" step) follow the event's routing:
/// <list type="bullet">
///   <item>A SELECTED-participant event (<see cref="SessionEvent.TargetParticipantId"/> set) is delivered
///   to that ONE participant's group plus the session hosts group — the selected participant receives it,
///   a non-selected participant (who is not in that group, CORE-RT-002) does NOT, and the hosts get the
///   confirmation (docs/11_REALTIME_SYNC.md test "host receives audit/confirmation event"; threat T3).</item>
///   <item>An AUDIENCE-WIDE event is delivered to the session hosts and observers groups. Fanning an
///   audience-wide event out to EACH participant's group with a per-recipient projected payload is the
///   recipient-specific projection story (CORE-RT-004); this story does not enumerate participants, so an
///   audience-wide event reaches hosts and observers here and the per-participant fan-out is deferred.</item>
/// </list>
///
/// The payload delivered is the recipient-safe <see cref="SessionEventEnvelope"/>, the SAME for every
/// recipient group (per-recipient payload projection is CORE-RT-004). Delivery is best-effort: the
/// append already committed the durable event, so a transient transport failure loses only the live push,
/// not the recorded fact (a reconnecting client replays it later, CORE-RT-005).
/// </summary>
internal sealed class SessionEventPublisher : ISessionEventPublisher
{
    private readonly ISessionEventRepository _events;
    private readonly IHubContext<SessionHub> _hub;

    public SessionEventPublisher(ISessionEventRepository events, IHubContext<SessionHub> hub)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(hub);
        _events = events;
        _hub = hub;
    }

    /// <inheritdoc />
    public async Task PublishAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // 1. Persist the durable event first (the source of truth for replay).
        await _events.AppendAsync(sessionEvent, cancellationToken).ConfigureAwait(false);

        // 2. Deliver the recipient-safe envelope to the server-computed recipient groups.
        var envelope = SessionEventEnvelope.From(sessionEvent);
        foreach (var group in ComputeRecipientGroups(sessionEvent))
        {
            await _hub.Clients
                .Group(group)
                .SendAsync(SessionEventEnvelope.ClientMethod, envelope, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Computes the server-managed groups an event is delivered to from its routing (see the type
    /// summary). The group NAMES come only from <see cref="RealtimeGroups"/>, never from any client input.
    /// </summary>
    private static IReadOnlyList<string> ComputeRecipientGroups(SessionEvent sessionEvent)
        => sessionEvent.TargetParticipantId is { } participantId
            ? new[]
            {
                RealtimeGroups.SessionParticipant(sessionEvent.SessionId, participantId),
                RealtimeGroups.SessionHosts(sessionEvent.SessionId),
            }
            : new[]
            {
                RealtimeGroups.SessionHosts(sessionEvent.SessionId),
                RealtimeGroups.SessionObservers(sessionEvent.SessionId),
            };
}
