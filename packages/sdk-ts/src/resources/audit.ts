/**
 * Audit resource group (CORE-SDK-006): the tenant's append-only audit log read
 * (csv/api_routes.csv "Read the tenant's append-only audit log"). The target
 * tenant is the required `organizationSlug` query parameter; the result is a
 * bounded page of PII/secret-free audit facts. Access is the privileged
 * Owner/Admin/Auditor roles; a non-privileged role is `403` and a foreign
 * tenant/non-member is hidden as `404` (a `LiveCoreApiError`), fail-closed
 * (docs/06_AUTHORIZATION_MATRIX.md; threat T7).
 */
import type { AuditLogPageResponse } from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class AuditClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/audit-logs` — the tenant's append-only audit log, in chronological
   * (append) order, as a bounded page. Pass optional `limit`/`offset` to page; the
   * result is the `entries + hasMore` envelope, never an unbounded array.
   */
  list(
    params: { organizationSlug: string } & PageParams,
  ): Promise<AuditLogPageResponse> {
    return this.http.send<AuditLogPageResponse>({
      method: "GET",
      path: "/audit-logs",
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }
}
