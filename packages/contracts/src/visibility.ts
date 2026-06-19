// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
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
 * Response body of a visibility rule (CORE-SVIS-005) — returned by the create
 * command and by the list and by-id read routes. A visibility rule is an authoring
 * artifact, so there is no host-vs-participant projection: the routes are restricted
 * to the authoring roles, so a participant never receives this shape.
 */
export interface VisibilityRuleResponse {
  /** The surrogate id of the visibility rule. */
  id: Uuid;
  /** The kind of resource the rule governs. */
  resourceType: VisibilityResourceType;
  /** The surrogate id of the resource the rule governs. */
  resourceId: Uuid;
  /** The base audience visibility state of the rule (`Hidden`/`Visible`). */
  visibility: VisibilityState;
  /**
   * The participant a selected-participant rule applies to, or `null` for an
   * audience-wide rule.
   */
  participantId: Uuid | null;
  /** Server timestamp (UTC) at which the rule was first created. */
  createdAt: IsoDateTimeString;
  /** Server timestamp (UTC) at which the rule was last updated. */
  updatedAt: IsoDateTimeString;
}

/**
 * A single participant-visible feed item. The feed is a skeleton today and is
 * always empty, so this item has no fields yet; its full, participant-safe shape
 * (a projected, already-filtered view) is defined by later Visibility/Reveal
 * stories. Modeled as an object with no members so it can never carry a
 * host-only or hidden field (docs/08_API_CONTRACTS.md; threats T2/T7).
 */
export type ParticipantVisibleFeedItem = Record<string, never>;

/**
 * Participant-safe response of `GET
 * /api/v1/participants/{participantId}/visible-feed`. The {@link items} list is
 * always empty in the current skeleton.
 */
export interface ParticipantVisibleFeedResponse {
  /** Surrogate id of the participant whose feed this is. */
  participantId: Uuid;
  /** Workspace the participant belongs to (a non-sensitive boundary id). */
  workspaceId: Uuid;
  /** The participant's currently visible feed items (always empty for now). */
  items: ParticipantVisibleFeedItem[];
  /** Server timestamp (UTC) at which this feed view was generated. */
  generatedAt: IsoDateTimeString;
}
