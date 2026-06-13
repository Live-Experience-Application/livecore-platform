/**
 * Sessions resource group (CORE-SDK-002): the by-session-id lifecycle commands
 * (`Prepared` → `Live` → `Ended`, plus `cancel` for a not-yet-started session).
 * The target organization is the required `organizationSlug` query parameter;
 * `start` requires the session to be `Prepared`, `end` requires it to be `Live`,
 * and `cancel` requires it to be `Prepared`, so any other state surfaces as a
 * `409` `LiveCoreApiError` (csv/api_routes.csv).
 */
import type { SessionResponse, Uuid } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class SessionsClient {
  constructor(private readonly http: HttpClient) {}

  /** `POST /api/v1/sessions/{sessionId}/start` — begin the live timeline. */
  start(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/start`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /** `POST /api/v1/sessions/{sessionId}/end` — end the live timeline. */
  end(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/end`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/cancel` — cancel a not-yet-started
   * (`Prepared`) session (CORE-LIFE-010). A soft, terminal `Prepared` → `Cancelled`
   * transition (never a delete), so the session's append-only history is preserved;
   * a session that is not `Prepared` surfaces as a `409` `LiveCoreApiError`.
   */
  cancel(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/cancel`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
