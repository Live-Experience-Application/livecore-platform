namespace LiveCore.Api.Realtime;

/// <summary>
/// One server-computed delivery of a session event (CORE-RT-004): a recipient-safe
/// <see cref="SessionEventEnvelope"/> addressed to exactly one SERVER-MANAGED group
/// (<see cref="RealtimeGroups"/>). It is the unit the <see cref="SessionEventRecipientResolver"/>
/// produces and the <see cref="SessionEventPublisher"/> sends: the resolver decides WHO receives the
/// event and WHICH projection they get (the host projection for the hosts group, the audience projection
/// for participants and observers — and only for recipients allowed to see the event's visibility
/// subject), and the publisher just delivers each pair. The group name comes only from
/// <see cref="RealtimeGroups"/>, never from client input (docs/11_REALTIME_SYNC.md).
/// </summary>
internal sealed record SessionEventDelivery(string Group, SessionEventEnvelope Envelope);
