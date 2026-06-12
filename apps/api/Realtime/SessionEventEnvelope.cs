namespace LiveCore.Api.Realtime;

/// <summary>
/// The recipient-safe envelope delivered to a connected client when a session event is published
/// (CORE-RT-003). It is the projected payload of the documented delivery flow ("...-> project payload ->
/// send to recipient groups", docs/11_REALTIME_SYNC.md). It carries only what a recipient may see: the
/// event id, the generic event type, the session it belongs to, the server-composed payload (resource
/// IDENTIFIERS only) and the schema version + timestamp. It deliberately EXCLUDES the internal addressing
/// fields of the stored <see cref="SessionEvent"/> — the organization and workspace ids, the
/// <c>createdBy</c> actor and the <c>targetParticipantId</c> routing — so a participant response never
/// carries internal authorization rationale (docs/08_API_CONTRACTS.md) and never reveals who else was
/// targeted (threats T2/T7).
///
/// This story delivers the SAME envelope to every recipient group of an event; computing a DIFFERENT
/// projected payload per recipient (the docs/09_EVENT_CATALOG.md <c>visibilityProjection</c>) is the
/// recipient-specific projection story (CORE-RT-004).
/// </summary>
internal sealed record SessionEventEnvelope(
    Guid EventId,
    string EventType,
    Guid SessionId,
    string Payload,
    int SchemaVersion,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// The SignalR client method invoked on the recipient connections to deliver an event. A single,
    /// server-fixed method name; clients subscribe to it to receive session events.
    /// </summary>
    public const string ClientMethod = "SessionEvent";

    /// <summary>Projects a stored <see cref="SessionEvent"/> to its recipient-safe envelope.</summary>
    public static SessionEventEnvelope From(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        return new SessionEventEnvelope(
            sessionEvent.Id,
            sessionEvent.EventType,
            sessionEvent.SessionId,
            sessionEvent.Payload,
            sessionEvent.SchemaVersion,
            sessionEvent.CreatedAt);
    }
}
