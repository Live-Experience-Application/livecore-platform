/**
 * Realtime resource group (CORE-SDK-002): reconnect replay of the durable
 * session event stream. A client rebuilds its live state from this REST route
 * after a disconnect; the server re-applies the same per-recipient filter, so a
 * hidden event is never replayed (docs/09_EVENT_CATALOG.md "Reconnect replay";
 * threat T3). A participant replaying its own feed identifies itself with
 * `participantId`, exactly like the live hub connection.
 */
import type { SessionEventReplayResponse, Uuid } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

/** Query parameters for the reconnect-replay route. */
export interface SessionEventReplayParams {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /**
   * The caller's own participant id, when replaying a participant feed. A caller
   * can only ever replay a participant feed they own (the server hides any other
   * as `404`).
   */
  participantId?: Uuid;
  /**
   * The caller's last acknowledged event id; events strictly after it are
   * replayed. Omit to replay the whole stream (the client deduplicates).
   */
  afterEventId?: Uuid;
}

export class RealtimeClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/sessions/{sessionId}/events` — the recipient-safe events the
   * caller is entitled to, in append order.
   */
  getSessionEvents(
    sessionId: Uuid,
    params: SessionEventReplayParams,
  ): Promise<SessionEventReplayResponse> {
    return this.http.send<SessionEventReplayResponse>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/events`,
      query: {
        organizationSlug: params.organizationSlug,
        participantId: params.participantId,
        afterEventId: params.afterEventId,
      },
    });
  }
}
