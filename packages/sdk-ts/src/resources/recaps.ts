/**
 * Recaps resource group (CORE-SDK-006): the role-projected read of a session's
 * most-recently-generated recap (csv/api_routes.csv). The target organization is
 * the required `organizationSlug` query parameter. The recap body is host content,
 * so the read is projected server-side by the caller's workspace role — the
 * host-content/metadata roles receive the full {@link RecapView} with the body,
 * the audience roles the host-only-field-stripped {@link RecapSummaryView} — so the
 * return type is the union of both shapes. A foreign tenant, unknown session,
 * non-member or a session with no recap yet is hidden as `404` (a
 * `LiveCoreApiError`), fail-closed (threats T2/T8).
 */
import type { RecapSummaryView, RecapView, Uuid } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class RecapsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/sessions/{sessionId}/recap` — the session's most-recently-generated
   * recap, projected by the caller's role.
   */
  getSessionRecap(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<RecapView | RecapSummaryView> {
    return this.http.send<RecapView | RecapSummaryView>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/recap`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
