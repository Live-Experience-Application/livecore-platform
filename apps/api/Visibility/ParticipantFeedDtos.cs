namespace LiveCore.Api.Visibility;

/// <summary>
/// Participant-SAFE response projection of a participant's visible feed
/// (CORE-SES-005, <c>GET /api/v1/participants/{participantId}/visible-feed</c>).
/// It is the body returned by the participant-visible feed route: the generic,
/// product-neutral, server-side view of what a single participant is currently
/// allowed to see.
///
/// SKELETON SCOPE — the feed is always EMPTY here. The route, its fail-closed
/// authorization and this participant-safe envelope are the deliverable of this
/// story; the ACTUAL visible content (filtered reveal events, content blocks and
/// the server-side visibility-rule evaluation) is produced by the later Visibility,
/// Reveal and Realtime epics (docs/05_MODULE_CONTRACTS.md: the Visibility module
/// owns "visibility rules", "audience calculations", "preview-as-participant" and
/// "visible state reconstruction", and provides
/// <c>GetVisibleResourcesForParticipant</c> — all LATER). There is no visibility
/// engine, no content and no session events yet (csv/database_tables.csv assigns
/// <c>session_events</c> to the Realtime module; "event append and delivery" is a
/// Realtime-epic story), so the feed is LEGITIMATELY empty: returning an empty
/// item list is the correct, fail-closed skeleton, not a stub that fabricates
/// content.
///
/// Participant-safe by construction (docs/08_API_CONTRACTS.md DTO design rules;
/// threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>It carries NO hidden content fields and NO host-only fields — a
///   participant DTO must never contain hidden content
///   (docs/08; "Participant DTOs must not contain hidden content fields").</item>
///   <item>It carries NO internal authorization rationale — it never echoes why
///   the caller was allowed (own-feed vs Host/CoHost preview) or how the
///   tenant/workspace was resolved ("Never include internal authorization
///   rationale in participant responses"; threat T7).</item>
///   <item>It includes a server timestamp (<see cref="GeneratedAt"/>) per docs/08
///   ("Include server timestamps").</item>
/// </list>
///
/// There is deliberately NO resource version/ETag field: the feed is a computed
/// read with no aggregate to version, and the per-event cursor a real feed will
/// carry (the last-acknowledged event id of docs/11_REALTIME_SYNC.md) belongs to
/// the Realtime epic's reconnect-replay model, not to this empty skeleton; it is
/// noted as a follow-up rather than fabricated here.
/// </summary>
/// <param name="ParticipantId">
/// Surrogate id of the participant whose feed this is (UUIDv7). Echoes the
/// addressed participant so a client can correlate the response.
/// </param>
/// <param name="WorkspaceId">
/// Workspace the participant belongs to. A non-sensitive boundary identifier,
/// already known to any caller entitled to this feed.
/// </param>
/// <param name="Items">
/// The participant's currently visible feed items. ALWAYS EMPTY in this skeleton
/// (see the type summary). The element type
/// (<see cref="ParticipantVisibleFeedItem"/>) is a minimal placeholder; its full
/// shape is defined by the later Visibility/Reveal stories.
/// </param>
/// <param name="GeneratedAt">
/// Server timestamp (UTC) at which this feed view was generated, from the
/// injected <see cref="TimeProvider"/>.
/// </param>
public sealed record ParticipantVisibleFeedResponse(
    Guid ParticipantId,
    Guid WorkspaceId,
    IReadOnlyList<ParticipantVisibleFeedItem> Items,
    DateTimeOffset GeneratedAt)
{
    /// <summary>
    /// Builds the participant-safe EMPTY feed envelope for the given participant
    /// at the given server time. The item list is empty by construction: there is
    /// no visibility engine and no content yet, so a participant entitled to this
    /// feed currently sees nothing (see the type summary). Only the non-sensitive
    /// boundary identifiers and the server timestamp are projected; nothing about
    /// the participant's user link, status or display name, and no authorization
    /// rationale, is ever echoed (threats T2/T7).
    /// </summary>
    public static ParticipantVisibleFeedResponse Empty(
        Guid participantId,
        Guid workspaceId,
        DateTimeOffset generatedAt)
        => new(participantId, workspaceId, [], generatedAt);
}

/// <summary>
/// A single participant-visible feed item (CORE-SES-005). MINIMAL placeholder:
/// the participant feed is always empty in this skeleton, so this type carries no
/// fields yet. Its full, participant-safe shape — the projected, already-filtered
/// view of a revealed content block or session event — is defined by the later
/// Visibility and Reveal stories (docs/05_MODULE_CONTRACTS.md;
/// docs/09_EVENT_CATALOG.md; docs/11_REALTIME_SYNC.md "Participant payload
/// projection").
///
/// It is deliberately defined as an empty record rather than guessing the
/// docs/09 event payload schema: inventing payload/content fields now would risk
/// shipping hidden-content fields a participant must never receive (docs/08 DTO
/// rules; threats T2/T7). Whatever fields it eventually gains MUST contain only
/// data already visible to the participant and MUST exclude any host-only or
/// hidden content (docs/07 primary security promise; docs/11 participant payload
/// projection).
/// </summary>
public sealed record ParticipantVisibleFeedItem;
