// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Identity & access resource group (CORE-SDK-006): the authenticated caller's
 * principal-context read (csv/api_routes.csv "Returns principal context only").
 * The response carries the caller's own profile and the organization memberships
 * the token claims — identifiers and the caller's own display metadata only, never
 * the access token or any secret (docs/07_SECURITY_THREAT_MODEL.md threat T7).
 */
import type { CurrentPrincipalResponse } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class IdentityClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/me` — the authenticated caller's principal context: their own
   * user profile plus the organization memberships (and role in each) the token
   * claims. A service-account caller is denied server-side.
   */
  getCurrentPrincipal(): Promise<CurrentPrincipalResponse> {
    return this.http.send<CurrentPrincipalResponse>({
      method: "GET",
      path: "/me",
    });
  }
}
