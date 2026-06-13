/**
 * Sessions resource group (CORE-SDK-002): the by-session-id lifecycle commands
 * (`Prepared` → `Live` → `Ended`). The target organization is the required
 * `organizationSlug` query parameter; `start` requires the session to be
 * `Prepared` and `end` requires it to be `Live`, so any other state surfaces as
 * a `409` `LiveCoreApiError` (csv/api_routes.csv).
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
}
