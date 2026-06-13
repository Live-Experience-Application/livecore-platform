namespace LiveCore.Api.Visibility;

/// <summary>
/// Participant-SAFE response projection of a participant's visible feed
/// (<c>GET /api/v1/participants/{participantId}/visible-feed</c>; the route skeleton
/// was CORE-SES-005, the real projection is CORE-API-005). It is the body returned
/// by the participant-visible feed route: the generic, product-neutral, server-side
/// view of what a single participant is currently allowed to see.
///
/// THE REAL PROJECTION (CORE-API-005). The feed now carries the participant's
/// ACTUALLY-VISIBLE resources, computed server-side by
/// <see cref="VisibilityPreviewService.GetVisibleResourcesForParticipantAsync"/> —
/// the participant-aware preview query (CORE-API-004), which routes every candidate
/// resource through the central <see cref="VisibilityPolicy"/> so a participant sees
/// a resource iff an audience-wide visible rule, or a visible rule scoped to exactly
/// them, applies. A resource revealed only to a DIFFERENT participant is excluded
/// (the selected-participant guarantee; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). The feed is empty only when the participant has
/// nothing visible yet — not by construction.
///
/// Participant-safe by construction (docs/08_API_CONTRACTS.md DTO design rules;
/// threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>It carries NO hidden content fields and NO host-only fields — a
///   participant DTO must never contain hidden content
///   (docs/08; "Participant DTOs must not contain hidden content fields"). Each item
///   is only the IDENTITY of a visible resource (kind + id), never its resolved
///   content — exactly the realtime audience projection
///   (<see cref="Realtime.SessionEventEnvelope.ForAudience"/>'s resource-identifier
///   payload), so REST and realtime describe a visible resource identically and can
///   never diverge.</item>
///   <item>It carries NO internal authorization rationale — it never echoes why
///   the caller was allowed (own-feed vs Host/CoHost preview) or how the
///   tenant/workspace was resolved ("Never include internal authorization
///   rationale in participant responses"; threat T7).</item>
///   <item>It includes a server timestamp (<see cref="GeneratedAt"/>) per docs/08
///   ("Include server timestamps").</item>
/// </list>
///
/// There is deliberately NO resource version/ETag field: the feed is a computed
/// read with no aggregate to version, and the per-event cursor a realtime feed
/// carries (the last-acknowledged event id of docs/11_REALTIME_SYNC.md) belongs to
/// the Realtime epic's reconnect-replay model
/// (<c>GET /api/v1/sessions/{sessionId}/events</c>), not to this point-in-time
/// visible-set projection.
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
/// The participant's currently visible feed items, in deterministic order (by
/// resource kind then id, as <see cref="VisibilityPreviewService"/> computes them).
/// Each is the participant-safe IDENTITY of a resource the participant may see.
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
    /// Builds the participant-safe feed envelope for the given participant from the
    /// resources the visibility engine computed as visible to THEM, at the given
    /// server time. Each <see cref="VisibleResource"/> is projected to a
    /// participant-safe <see cref="ParticipantVisibleFeedItem"/> (kind + id only),
    /// preserving the engine's deterministic order. Only the non-sensitive boundary
    /// identifiers, the visible-resource identities and the server timestamp are
    /// projected; nothing about the participant's user link, status or display name,
    /// and no authorization rationale, is ever echoed (threats T2/T7). When the
    /// participant has nothing visible the item list is naturally empty.
    /// </summary>
    public static ParticipantVisibleFeedResponse From(
        Guid participantId,
        Guid workspaceId,
        IReadOnlyList<VisibleResource> visibleResources,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(visibleResources);

        var items = new ParticipantVisibleFeedItem[visibleResources.Count];
        for (var index = 0; index < visibleResources.Count; index++)
        {
            items[index] = ParticipantVisibleFeedItem.From(visibleResources[index]);
        }

        return new ParticipantVisibleFeedResponse(participantId, workspaceId, items, generatedAt);
    }
}

/// <summary>
/// A single participant-visible feed item (CORE-API-005). It names a resource the
/// participant may currently see GENERICALLY by its kind and surrogate id, exactly as
/// a <see cref="VisibilityRule"/> and the realtime audience event payload
/// (<see cref="Realtime.SessionEventEnvelope.ForAudience"/>) address their resource —
/// so the REST feed and the realtime stream describe a visible resource with the SAME
/// (resource type, resource id) shape and can never diverge.
///
/// It carries ONLY the resource IDENTITY, never the resolved content, payload or any
/// host-only field: projecting an item can therefore never leak hidden content (the
/// primary security promise of docs/07_SECURITY_THREAT_MODEL.md; docs/08 DTO rules;
/// threats T2/T7). Resolving an identity into the participant-safe rendered content
/// (text/media/data) is the Realtime/content-delivery concern of a later story; this
/// item is the visible-resource handle the participant feed returns.
///
/// <see cref="ResourceType"/> is the STABLE NAME of a
/// <see cref="VisibilityResourceType"/> (Scene/ContentBlock/Entity), serialized by
/// name exactly like <see cref="RevealResponse.ResourceType"/> and the realtime reveal
/// payload, never by its numeric value.
/// </summary>
/// <param name="ResourceType">
/// The kind of the visible resource — the name of a <see cref="VisibilityResourceType"/>
/// (Scene/ContentBlock/Entity).
/// </param>
/// <param name="ResourceId">The surrogate id of the visible resource.</param>
public sealed record ParticipantVisibleFeedItem(string ResourceType, Guid ResourceId)
{
    /// <summary>
    /// Projects a computed <see cref="VisibleResource"/> (the visibility engine's
    /// visible-resource handle) into its participant-safe feed item: the resource
    /// kind as its stable name plus the surrogate id. Identifiers only — never the
    /// resolved content (threat T7).
    /// </summary>
    public static ParticipantVisibleFeedItem From(VisibleResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new ParticipantVisibleFeedItem(resource.ResourceType.ToString(), resource.ResourceId);
    }
}
