// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// The participant-feed VIEW of a single resource that is visible to a specific participant in a session
/// (CORE-APROJ-002) — the enrichment of the bare <see cref="VisibleResource"/> identity with the
/// audience-safe REVEAL metadata the participant-visible feed needs to render an item without a host read.
/// It is computed by the central <see cref="VisibilityPolicy.ComputeVisibleResourceRevealsForParticipant"/>
/// from the SAME rules that decide whether the participant may see the resource, so the feed's reveal
/// metadata can never diverge from the visibility decision (docs/05_MODULE_CONTRACTS.md: visibility lives
/// in one place). The audience-safe LABEL/BODY are NOT on this type: they are resolved separately from the
/// owning resource modules through the <see cref="IVisibleResourceAudienceProjector"/> port (the central
/// security module may not reference Scenes/Content/Entities — CORE-ARCH-001), then combined with this
/// reveal into the response item.
///
/// Every field is audience-SAFE — the resource IDENTITY (kind + id), the reveal TIME and the reveal SCOPE
/// marker — never the resolved host content, payload or any host-only field, so projecting it can never
/// leak hidden content (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="ResourceType">The kind of the visible resource (Scene/ContentBlock/Entity).</param>
/// <param name="ResourceId">The surrogate id of the visible resource.</param>
/// <param name="RevealedAt">
/// When the resource became visible to the participant (UTC) — the reveal time (the granting rule's last
/// visibility transition) of the EARLIEST granting rule of the effective <see cref="Scope"/>. A timestamp,
/// never content.
/// </param>
/// <param name="Scope">
/// Whether the resource is visible to the participant through an audience-wide reveal or only through a
/// reveal scoped to exactly them (<see cref="VisibleResourceRevealScope"/>).
/// </param>
/// <param name="Locked">
/// Whether the granting rule of the effective <see cref="Scope"/> is SEALED (locked, CORE-VSEAL-001): the
/// resource is permanently-restricted in the way it is visible to the participant, so a consumer can render a
/// locked presentation state bound to this server fact. It is audience-safe — a boolean server fact about a
/// resource the participant already sees, never host content (threat T7). <see langword="false"/> when no
/// granting rule of the effective scope is locked.
/// </param>
/// <param name="ScheduledRevealAt">
/// The optional SCHEDULED-REVEAL time (UTC) of the granting rule of the effective <see cref="Scope"/>
/// (CORE-VSEAL-002), or <see langword="null"/> when no granting rule of that scope carries a schedule. It lets a
/// consumer render a scheduled presentation state (the WHEN the resource was scheduled to appear). It is
/// audience-safe — a timestamp server fact about a resource the participant already sees, never host content
/// (threat T7).
/// </param>
internal sealed record VisibleResourceReveal(
    VisibilityResourceType ResourceType,
    Guid ResourceId,
    DateTimeOffset RevealedAt,
    VisibleResourceRevealScope Scope,
    bool Locked,
    DateTimeOffset? ScheduledRevealAt);
