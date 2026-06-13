/**
 * Entitlements resource group (CORE-SDK-002): the server-calculated quota status
 * and ad-eligibility a mobile paywall or feature guard consumes. The premium and
 * ad-free state comes ONLY from the server — a client must never trust a local
 * flag (docs/21, docs/22). Core returns no ad placements or provider config.
 */
import type {
  AdEligibilityResponse,
  QuotaStatusResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class EntitlementsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/me/quota-status` — the current user's server-calculated quota
   * status.
   */
  getMyQuotaStatus(): Promise<QuotaStatusResponse> {
    return this.http.send<QuotaStatusResponse>({
      method: "GET",
      path: "/me/quota-status",
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/quota-status` — a workspace's
   * server-calculated quota status (membership authorized).
   */
  getWorkspaceQuotaStatus(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<QuotaStatusResponse> {
    return this.http.send<QuotaStatusResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/quota-status`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/me/ad-eligibility` — the current user's entitlement-driven ad
   * eligibility (fail-closed default: ads required).
   */
  getMyAdEligibility(): Promise<AdEligibilityResponse> {
    return this.http.send<AdEligibilityResponse>({
      method: "GET",
      path: "/me/ad-eligibility",
    });
  }
}
