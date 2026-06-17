// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// A single Core resource that is visible to the audience (CORE-VIS-003) — one element of a
/// preview-as-participant result (<see cref="VisibilityPreviewService"/>). It names the resource
/// generically by its kind and surrogate id (<see cref="ResourceType"/> + <see cref="ResourceId"/>),
/// exactly as a <see cref="VisibilityRule"/> addresses its resource; it carries NO payload, content
/// or host-only field — it is only the IDENTITY of a resource the audience may see, so projecting it
/// can never leak hidden content (threats T2/T7 in docs/07_SECURITY_THREAT_MODEL.md). Resolving each
/// reference into a participant-safe content projection is the Realtime/feed-delivery concern of a
/// later story; this type is just the visible-resource handle the Visibility module computes.
/// </summary>
/// <param name="ResourceType">The kind of the visible resource (Scene/ContentBlock/Entity).</param>
/// <param name="ResourceId">The surrogate id of the visible resource.</param>
public sealed record VisibleResource(VisibilityResourceType ResourceType, Guid ResourceId);
