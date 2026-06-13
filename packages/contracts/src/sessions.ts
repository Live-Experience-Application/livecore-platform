import type { SessionStatus } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Sessions module contracts (CORE-SDK-001). The session response is the generic,
 * product-neutral view returned by the start/end lifecycle commands
 * (`POST /api/v1/sessions/{sessionId}/start` and `.../end`).
 */

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
