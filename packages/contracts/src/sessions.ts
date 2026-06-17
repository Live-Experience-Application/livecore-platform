// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { ParticipantPresenceOutcome, SessionStatus } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Sessions module contracts (CORE-SDK-001). The session response is the generic,
 * product-neutral view returned by the create, by-id read and start/end/cancel
 * lifecycle commands (`POST /api/v1/sessions/{sessionId}/start` and `.../end`).
 */

/**
 * Request body for `POST /api/v1/workspaces/{workspaceId}/sessions` (CORE-SDK-006).
 * The workspace is taken from the route path. A session is always created
 * `Prepared`, so the body carries no lifecycle status — the only way into the live
 * timeline is the guarded start command.
 */
export interface CreateSessionRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /** Human-readable display title of the new session. */
  title: string;
}

/** Response projection of a session after a lifecycle transition. */
export interface SessionResponse {
  /** Surrogate id of the session. */
  id: Uuid;
  /** Tenant the session belongs to. */
  organizationId: Uuid;
  /** Workspace the session belongs to. */
  workspaceId: Uuid;
  /** Human-readable display title of the session. */
  title: string;
  /** Lifecycle status name after the applied transition. */
  status: SessionStatus;
  /** When the live timeline started (UTC), or `null` while still `Prepared`. */
  startedAt: IsoDateTimeString | null;
  /** When the live timeline ended (UTC), or `null` until the session is `Ended`. */
  endedAt: IsoDateTimeString | null;
  /** When the session was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the session was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}

/**
 * Response projection of a host-driven participant presence command (CORE-SDK-006,
 * `POST /api/v1/sessions/{sessionId}/participants/{participantId}/join` and
 * `.../leave`). It is identifier-only and product-neutral — the session and
 * participant ids and the generic outcome name only, never a participant display
 * name or any PII (threat T7) — exactly the identifier-only envelope the persisted
 * `ParticipantJoined`/`ParticipantLeft` event payload carries.
 */
export interface ParticipantPresenceResponse {
  /** Surrogate id of the session the presence change applied to. */
  sessionId: Uuid;
  /** Surrogate id of the participant that joined or left. */
  participantId: Uuid;
  /**
   * Generic presence outcome name: `Joined` (admitted), `Left` (removed and the
   * `ParticipantLeft` event delivered) or `AlreadyLeft` (the idempotent no-op).
   */
  outcome: ParticipantPresenceOutcome;
}
