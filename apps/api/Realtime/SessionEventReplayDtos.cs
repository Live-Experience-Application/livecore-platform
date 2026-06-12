namespace LiveCore.Api.Realtime;

/// <summary>
/// Recipient-safe response of the reconnect-replay route (CORE-RT-005,
/// <c>GET /api/v1/sessions/{sessionId}/events</c>) — the REST projection of the durable session event
/// stream a reconnecting client replays. It realizes the documented reconnect flow
/// (docs/09_EVENT_CATALOG.md "Reconnect replay": authenticate -&gt; resolve context -&gt; resolve
/// participant identity -&gt; query events after the last acknowledged event -&gt; filter each event
/// through the Visibility module -&gt; send only projected recipient-safe payloads;
/// docs/11_REALTIME_SYNC.md "Offline/reconnect": last acknowledged event id + server-side replay filter +
/// duplicate event handling).
///
/// PARTICIPANT-SAFE BY CONSTRUCTION (docs/08_API_CONTRACTS.md DTO rules; threats T2/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). Each <see cref="SessionEventReplayItem"/> carries the SAME fields as
/// the live <see cref="SessionEventEnvelope"/> the realtime hub delivers — the event id, the generic event
/// type, the session, the server-composed payload (resource IDENTIFIERS only), the schema version and the
/// timestamp — and is built by the SAME per-recipient projection (<see cref="SessionEventEnvelope.ForHost"/>
/// vs <see cref="SessionEventEnvelope.ForAudience"/>), so a replayed item is byte-for-byte the projection
/// the recipient would have received live. It deliberately EXCLUDES the stored event's internal addressing
/// fields (the organization/workspace ids, the <c>createdBy</c> actor and the visibility-subject gating
/// fields), and the audience projection omits the routing target, so a participant never learns who else a
/// private reveal targeted (only the host projection carries it as the "to whom" confirmation hosts are
/// entitled to). No authorization rationale is ever echoed.
///
/// WHICH events appear, and WHICH projection each gets, is decided entirely by the central Visibility
/// engine reused through the live recipient resolver (<see cref="SessionReplayService"/>), so reconnect
/// replay can never leak an event live delivery would have withheld ("reconnect replay filters events
/// again", threat T3).
/// </summary>
/// <param name="SessionId">The session whose stream was replayed (echoes the addressed session).</param>
/// <param name="Events">
/// The recipient-safe events the caller is entitled to, in append (chronological) order — only those
/// after the acknowledged cursor when one was supplied. Always present; empty when the caller has
/// acknowledged everything they may see.
/// </param>
/// <param name="GeneratedAt">Server timestamp (UTC) at which the replay was computed.</param>
public sealed record SessionEventReplayResponse(
    Guid SessionId,
    IReadOnlyList<SessionEventReplayItem> Events,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// Builds the replay response from the already-filtered, already-projected recipient envelopes
    /// (<see cref="SessionReplayService.ReplayAsync"/>) at the given server time. This is a pure shape
    /// mapping — every visibility decision and projection has already been made — so no event can be
    /// added or unmasked here.
    /// </summary>
    internal static SessionEventReplayResponse From(
        Guid sessionId,
        IReadOnlyList<SessionEventEnvelope> envelopes,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        var items = new List<SessionEventReplayItem>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            items.Add(SessionEventReplayItem.From(envelope));
        }

        return new SessionEventReplayResponse(sessionId, items, generatedAt);
    }
}

/// <summary>
/// A single recipient-safe replay item (CORE-RT-005). Its fields mirror the live
/// <see cref="SessionEventEnvelope"/> exactly, so a client can process a replayed item and a live event
/// through one code path. <see cref="TargetParticipantId"/> is populated only on the HOST projection (the
/// routing confirmation); the audience projection leaves it <see langword="null"/>, so an audience
/// recipient never learns who else was targeted (threats T2/T7).
/// </summary>
/// <param name="EventId">Surrogate id of the event (UUIDv7); the client acknowledges the latest it processes.</param>
/// <param name="EventType">The generic, product-neutral event type (docs/09_EVENT_CATALOG.md).</param>
/// <param name="SessionId">The session the event belongs to.</param>
/// <param name="Payload">The server-composed payload — resource identifiers only, never resolved content (threat T7).</param>
/// <param name="SchemaVersion">The payload schema version (docs/09_EVENT_CATALOG.md).</param>
/// <param name="CreatedAt">When the event happened (UTC).</param>
/// <param name="TargetParticipantId">
/// The routing target on the host projection (the "to whom" confirmation), or <see langword="null"/> on
/// the audience projection and for an audience-wide event.
/// </param>
public sealed record SessionEventReplayItem(
    Guid EventId,
    string EventType,
    Guid SessionId,
    string Payload,
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    Guid? TargetParticipantId)
{
    /// <summary>
    /// Projects a recipient-safe <see cref="SessionEventEnvelope"/> (already gated and projected by the
    /// replay filter) to its REST item. A pure field copy: it makes no visibility decision and reveals
    /// nothing the envelope did not already carry.
    /// </summary>
    internal static SessionEventReplayItem From(SessionEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new SessionEventReplayItem(
            envelope.EventId,
            envelope.EventType,
            envelope.SessionId,
            envelope.Payload,
            envelope.SchemaVersion,
            envelope.CreatedAt,
            envelope.TargetParticipantId);
    }
}
