// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
    /// Builds the participant-safe feed envelope for the given participant from the already-projected,
    /// audience-safe feed items (CORE-APROJ-002), at the given server time, preserving the engine's
    /// deterministic order. The items are built by the endpoint, which combines each
    /// <see cref="VisibleResourceReveal"/> the visibility engine computed with the resource's audience-safe
    /// label/body resolved through the <see cref="IVisibleResourceAudienceProjector"/> port (the central
    /// security module may not reference the resource modules — CORE-ARCH-001). Only the non-sensitive
    /// boundary identifiers, the audience-safe items and the server timestamp are projected; nothing about
    /// the participant's user link, status or display name, and no authorization rationale, is ever echoed
    /// (threats T2/T7). When the participant has nothing visible the item list is naturally empty.
    /// </summary>
    public static ParticipantVisibleFeedResponse From(
        Guid participantId,
        Guid workspaceId,
        IReadOnlyList<ParticipantVisibleFeedItem> items,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new ParticipantVisibleFeedResponse(participantId, workspaceId, items, generatedAt);
    }
}

/// <summary>
/// A single participant-visible feed item (CORE-API-005; ENRICHED with audience-safe projected fields by
/// CORE-APROJ-002). It names a resource the participant may currently see GENERICALLY by its kind and
/// surrogate id — exactly as a <see cref="VisibilityRule"/> and the realtime audience event payload
/// (<see cref="Realtime.SessionEventEnvelope.ForAudience"/>) address their resource — and additionally
/// carries the AUDIENCE-SAFE projected fields that let a consumer render the revealed item from the feed
/// ALONE, with no host read: an audience-safe <see cref="Title"/> (and, where the kind has one, a short
/// <see cref="Body"/>), the <see cref="RevealedAt"/> reveal time and the <see cref="RevealScope"/> marker.
///
/// AUDIENCE-SAFE BY CONSTRUCTION (docs/08 DTO rules; threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>The <see cref="Title"/>/<see cref="Body"/> are produced ONLY through the resource kind's existing
///   role-based AUDIENCE projection (<c>ParticipantSceneResponse</c>/<c>ParticipantContentBlockResponse</c>/
///   <c>ParticipantEntityResponse</c>), resolved through the <see cref="IVisibleResourceAudienceProjector"/>
///   port, NEVER the raw host title/body — so no host-only content can leak (threat T2). No current resource
///   kind's audience projection exposes a body (the content block's body is the host-only payload its
///   audience projection strips), so <see cref="Body"/> is the forward-compatible audience-body slot and is
///   null today.</item>
///   <item>It carries NO internal authorization rationale and NO host-only field — it never echoes why the
///   caller was allowed nor how the tenant/workspace was resolved (threat T7).</item>
///   <item>Visibility is NEVER recomputed here: the item is built only for a resource the central
///   <see cref="VisibilityPolicy"/> has already decided the participant may see (the feed is already-filtered
///   and fail-closed), so the participant only ever receives items it may see.</item>
/// </list>
///
/// <see cref="ResourceType"/> and <see cref="RevealScope"/> are STABLE enum NAMES (the
/// <see cref="VisibilityResourceType"/> and the <see cref="VisibleResourceRevealScope"/>), serialized by
/// name exactly like <see cref="RevealResponse.ResourceType"/> and the realtime reveal payload, never by a
/// numeric value.
/// </summary>
/// <param name="ResourceType">
/// The kind of the visible resource — the name of a <see cref="VisibilityResourceType"/>
/// (Scene/ContentBlock/Entity).
/// </param>
/// <param name="ResourceId">The surrogate id of the visible resource.</param>
/// <param name="Title">
/// The resource's audience-safe label (a scene's title, an entity's name, a content block's generic kind),
/// or <see langword="null"/> when the resource no longer resolves (a dangling rule). Produced only through
/// the kind's audience projection — never the raw host title.
/// </param>
/// <param name="Body">
/// The resource's audience-safe short body, or <see langword="null"/> when the kind's audience projection
/// exposes none (the case for every current Core resource kind). Never the raw host body (threat T2).
/// </param>
/// <param name="RevealedAt">
/// When the resource became visible to the participant (UTC) — the reveal time of the granting rule. A
/// timestamp, never content.
/// </param>
/// <param name="RevealScope">
/// The marker distinguishing an audience-wide reveal from a selected-participant reveal — the stable name of
/// a <see cref="VisibleResourceRevealScope"/> (AudienceWide/SelectedParticipant).
/// </param>
public sealed record ParticipantVisibleFeedItem(
    string ResourceType,
    Guid ResourceId,
    string? Title,
    string? Body,
    DateTimeOffset RevealedAt,
    string RevealScope)
{
    /// <summary>
    /// Projects a computed <see cref="VisibleResourceReveal"/> (the visibility engine's enriched
    /// visible-resource handle) together with the resource's audience-safe label/body
    /// (<paramref name="projection"/>, resolved through the <see cref="IVisibleResourceAudienceProjector"/>
    /// port, or <see langword="null"/> when the resource no longer resolves) into its participant-safe feed
    /// item: the resource kind and reveal scope as their stable names, the surrogate id, the reveal time and
    /// the audience-safe title/body. Audience-safe only — never the raw host title/body or any host-only
    /// field (threats T2/T7). The factory is internal because it consumes the Visibility module's internal
    /// reveal/projection types; the DTO itself is public for serialization and the published contract.
    /// </summary>
    internal static ParticipantVisibleFeedItem From(
        VisibleResourceReveal reveal,
        VisibleResourceAudienceProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(reveal);

        return new ParticipantVisibleFeedItem(
            reveal.ResourceType.ToString(),
            reveal.ResourceId,
            projection?.Title,
            projection?.Body,
            reveal.RevealedAt,
            reveal.Scope.ToString());
    }
}
