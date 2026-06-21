// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
  FeedRevealScope,
  HideOutcome,
  RevealOutcome,
  VisibilityResourceType,
  VisibilityState,
} from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Visibility module contracts (CORE-SDK-001): the reveal/hide commands and the
 * participant-visible feed. The reveal and hide commands are idempotent — a retry
 * with the same `Idempotency-Key` header produces no duplicate effect
 * (docs/08_API_CONTRACTS.md).
 */

/**
 * Request body for `POST /api/v1/sessions/{sessionId}/reveal`. The client's
 * retry-safety token is the `Idempotency-Key` request header, not a body field.
 */
export interface RevealRequest {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /** The kind of resource to reveal (`Scene`/`ContentBlock`/`Entity`). */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource to reveal. */
  resourceId: Uuid;
  /**
   * Optional target of a selected-participant reveal. When set, the resource is
   * revealed only to that participant; when omitted, it is revealed to the whole
   * audience.
   */
  participantId?: Uuid;
}

/** Response body of the reveal command. */
export interface RevealResponse {
  /** The kind of resource that was revealed. */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource that was revealed. */
  resourceId: Uuid;
  /** Always `true` after a successful reveal. */
  visible: boolean;
  /** Whether the reveal was newly applied or recognized as an idempotent retry. */
  outcome: RevealOutcome;
  /**
   * The participant the resource was revealed to (a selected-participant reveal),
   * or `null` when it was revealed to the whole audience.
   */
  participantId: Uuid | null;
}

/**
 * Request body for `POST /api/v1/sessions/{sessionId}/hide` (CORE-SDK-006). It
 * mirrors {@link RevealRequest} exactly with the opposite visibility sense; the
 * client's retry-safety token is the `Idempotency-Key` request header, not a body
 * field.
 */
export interface HideRequest {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /** The kind of resource to hide (`Scene`/`ContentBlock`/`Entity`). */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource to hide. */
  resourceId: Uuid;
  /**
   * Optional target of a selected-participant hide. When set, the resource is
   * hidden only for that participant; when omitted, it is hidden from the whole
   * audience.
   */
  participantId?: Uuid;
}

/** Response body of the hide command. Mirrors {@link RevealResponse} inverted. */
export interface HideResponse {
  /** The kind of resource that was hidden. */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource that was hidden. */
  resourceId: Uuid;
  /** Always `false` after a successful hide. */
  visible: boolean;
  /** Whether the hide was newly applied or recognized as an idempotent retry. */
  outcome: HideOutcome;
  /**
   * The participant the resource was hidden for (a selected-participant hide), or
   * `null` when it was hidden from the whole audience.
   */
  participantId: Uuid | null;
}

/**
 * Request body for `POST /api/v1/sessions/{sessionId}/visibility-rules`
 * (CORE-SVIS-005): an authoring role creates a session-scoped visibility rule for
 * a resource. The same-workspace invariant for the referenced resource is enforced
 * server-side (a resource from another workspace is rejected with `400`); the
 * session id in the path pins the workspace.
 */
export interface CreateVisibilityRuleRequest {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /** The kind of resource the rule governs (`Scene`/`ContentBlock`/`Entity`). */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource the rule governs (resolved within the workspace). */
  resourceId: Uuid;
  /** The base audience visibility the rule assigns (`Hidden`/`Visible`). */
  visibility: VisibilityState;
  /**
   * Optional target of a selected-participant rule. When set, the rule applies
   * only to that participant; when omitted, it applies to the whole audience. The
   * target must be a participant of the session's workspace (otherwise hidden as
   * `404`).
   */
  participantId?: Uuid;
}

/**
 * Response body of a visibility rule (CORE-SVIS-005; ENRICHED with an audience-safe
 * resource label in CORE-APROJ-004) — returned by the create command and by the list
 * and by-id read routes. A visibility rule is an authoring artifact, so there is no
 * host-vs-participant projection: the routes are restricted to the authoring roles, so
 * a participant never receives this shape.
 *
 * It carries a denormalized, audience-safe {@link resourceLabel} for the governed
 * resource, so a host rendering a per-resource visibility matrix from `listRules` can
 * name each row from the rule list ALONE, with no per-resource host read. The label is
 * the resource's audience-safe NAME (a scene's title, an entity's name, a content
 * block's generic kind), never its full host content (threats T2/T7).
 *
 * It also carries the sealed/locked authoring flag (CORE-VSEAL-001): a server-asserted
 * boolean that marks the governed resource permanently-restricted, so a reveal/hide/change
 * targeting a locked rule is refused with `409`. The flag is ORTHOGONAL to the
 * Hidden/Visible {@link visibility} state — not a third state — and is set/cleared only by
 * the authoring roles through the lock/unlock commands.
 */
export interface VisibilityRuleResponse {
  /** The surrogate id of the visibility rule. */
  id: Uuid;
  /** The kind of resource the rule governs. */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource the rule governs. */
  resourceId: Uuid;
  /**
   * The governed resource's denormalized, audience-safe label (a scene's title, an
   * entity's name, a content block's generic kind), or `null` when the resource no
   * longer resolves in the rule's own workspace (a dangling rule whose resource was
   * deleted). Produced only through the resource kind's audience projection — never
   * its full host content.
   */
  resourceLabel: string | null;
  /** The base audience visibility state of the rule (`Hidden`/`Visible`). */
  visibility: VisibilityState;
  /**
   * The participant a selected-participant rule applies to, or `null` for an
   * audience-wide rule.
   */
  participantId: Uuid | null;
  /**
   * Whether the rule is SEALED (locked) — the server-asserted authoring lock
   * (CORE-VSEAL-001) that makes the governed resource permanently-restricted: while
   * `true`, a reveal/hide/change targeting the rule is refused with `409`. An orthogonal
   * flag, not a third {@link visibility} state, so a consumer can render a locked
   * presentation state while the binary Hidden/Visible state is unchanged. `false` for an
   * unlocked rule.
   */
  locked: boolean;
  /** Server timestamp (UTC) at which the rule was first created. */
  createdAt: IsoDateTimeString;
  /** Server timestamp (UTC) at which the rule was last updated. */
  updatedAt: IsoDateTimeString;
}

/**
 * A single AUDIENCE-SAFE attachment of a participant-visible feed item (CORE-ALC-002):
 * an asset attached to the visible resource through the existing asset-link join
 * (CORE-AST-005). It lets an audience surface enumerate the attachments of content the
 * participant has been shown and then request the authorized per-asset download.
 *
 * It is audience-SAFE by construction (docs/08_API_CONTRACTS.md DTO rules; threats
 * T2/T4/T7): it carries ONLY the asset's surrogate {@link assetId} (the download
 * handle), an audience-safe {@link name} and the {@link contentType} — never a host-only
 * field (the storage provider/bucket/object key and the checksum are host-only and never
 * participant-facing). The attachment is listed only for a resource the participant may
 * already see, so it inherits the resource's audience visibility (a hidden resource's
 * attachments are never enumerated). Listing an attachment never grants access to its
 * bytes: the download still goes through the server-side `getDownloadUrl` authorization
 * check (defence in depth). Mirrors the server DTO
 * `ParticipantVisibleFeedAttachment(Guid AssetId, string? Name, string ContentType)`.
 */
export interface ParticipantVisibleFeedAttachment {
  /**
   * The surrogate id of the attached asset — the handle a consumer passes to
   * `getDownloadUrl` to request the authorized download.
   */
  assetId: Uuid;
  /**
   * The asset's audience-safe display label, or `null` when it has none. Core stores no
   * host-facing asset filename and its storage coordinates are host-only, so this is the
   * forward-compatible audience-safe label slot and is `null` today (exactly as the feed
   * item's audience {@link ParticipantVisibleFeedItem.body} slot is).
   */
  name: string | null;
  /**
   * The attached asset's MIME content type — the same non-sensitive metadata the signed
   * download-url response already discloses to an authorized audience caller.
   */
  contentType: string;
}

/**
 * A single participant-visible feed item (CORE-API-005; aligned to the server DTO in
 * CORE-APROJ-001; ENRICHED with audience-safe projected fields in CORE-APROJ-002; an
 * audience-safe attachments list added in CORE-ALC-002). It names a resource the
 * participant may currently see by its kind and surrogate id — exactly as a
 * {@link RevealResponse} and the realtime audience event address their resource — and
 * additionally carries the audience-safe fields that let a consumer render the revealed
 * item from the feed ALONE, with no host read: an audience-safe {@link title} (and, where
 * the kind has one, a short {@link body}), the {@link revealedAt} reveal time, the
 * {@link revealScope} marker and the {@link attachments} list.
 *
 * It is audience-SAFE by construction (docs/08_API_CONTRACTS.md DTO rules; threats
 * T2/T7): the title/body are produced ONLY through the resource kind's existing
 * role-based audience projection, never the raw host title/body, so projecting an item
 * can never leak hidden content. The {@link locked} flag (CORE-VSEAL-001) is an
 * audience-safe boolean server fact about a resource the participant is already allowed to
 * see — never host content. Mirrors the server DTO
 * `ParticipantVisibleFeedItem(string ResourceType, Guid ResourceId, string? Title,
 * string? Body, DateTimeOffset RevealedAt, string RevealScope, bool Locked,
 * IReadOnlyList<ParticipantVisibleFeedAttachment> Attachments)`.
 */
export interface ParticipantVisibleFeedItem {
  /**
   * The kind of the visible resource — the stable name of a
   * {@link VisibilityResourceType} (`Scene`/`ContentBlock`/`Entity`).
   */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the visible resource. */
  resourceId: Uuid;
  /**
   * The resource's audience-safe label (a scene's title, an entity's name, a content
   * block's generic kind), or `null` when the resource no longer resolves. Produced
   * only through the kind's audience projection — never the raw host title.
   */
  title: string | null;
  /**
   * The resource's audience-safe short body, or `null` when the kind's audience
   * projection exposes none (the case for every current Core resource kind — a content
   * block's host body is never disclosed here). Never the raw host body.
   */
  body: string | null;
  /** When the resource became visible to the participant (the reveal time). */
  revealedAt: IsoDateTimeString;
  /**
   * Whether the resource is visible through an audience-wide reveal or only a reveal
   * scoped to exactly this participant ({@link FeedRevealScope}).
   */
  revealScope: FeedRevealScope;
  /**
   * Whether the visible resource is SEALED (locked) in the way this participant sees it
   * (CORE-VSEAL-001) — a server-asserted authoring lock marking it permanently-restricted,
   * so an audience surface can render a locked presentation state bound to this server
   * fact. Audience-safe: a boolean fact about an already-visible resource, never host
   * content. `false` for a normally-revealed (unlocked) resource.
   */
  locked: boolean;
  /**
   * The audience-safe list of assets attached to this resource (CORE-ALC-002), each an
   * assetId plus an audience-safe name and content type. Empty (never absent) when the
   * resource has no attachments. Each attachment inherits this resource's audience
   * visibility, so a hidden resource's attachments are never enumerated (threat T2).
   */
  attachments: ParticipantVisibleFeedAttachment[];
}

/**
 * Participant-safe response of `GET
 * /api/v1/participants/{participantId}/visible-feed`. The {@link items} list is the
 * participant's currently visible resources — each the participant-safe identity of
 * a resource they may see — in the server's deterministic order; it is empty only
 * when the participant has nothing visible yet.
 */
export interface ParticipantVisibleFeedResponse {
  /** Surrogate id of the participant whose feed this is. */
  participantId: Uuid;
  /** Workspace the participant belongs to (a non-sensitive boundary id). */
  workspaceId: Uuid;
  /** The participant's currently visible feed items, in deterministic order. */
  items: ParticipantVisibleFeedItem[];
  /** Server timestamp (UTC) at which this feed view was generated. */
  generatedAt: IsoDateTimeString;
}
