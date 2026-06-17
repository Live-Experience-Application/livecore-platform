// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Organizations resource group (CORE-SDK-006): the tenant-root create/list routes
 * and the tenant-offboarding/data-protection deletes (csv/api_routes.csv). The
 * organization is the Core-owned, product-neutral tenant root. The server
 * authorizes every call; a caller who may not see a tenant is hidden as `404` (a
 * `LiveCoreApiError`), never `403` (docs/06_AUTHORIZATION_MATRIX.md).
 *
 * The member personal-data routes are GDPR data-subject controls: an erasure
 * (Art.17) anonymizes the subject's personal data, and a personal-data export
 * (Art.15/20) returns the subject's own data and is delivered only to the entitled
 * recipient. The export carries PII by design but never a secret (threats T6/T7).
 */
import type {
  CreateOrganizationRequest,
  OrganizationResponse,
  PersonalDataExportResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class OrganizationsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/organizations` — the organizations the caller is a member of (and
   * whose tenant the token claims). A service-account caller is denied server-side.
   */
  list(): Promise<OrganizationResponse[]> {
    return this.http.send<OrganizationResponse[]>({
      method: "GET",
      path: "/organizations",
    });
  }

  /**
   * `POST /api/v1/organizations` — create a new tenant and the caller's founding
   * `Owner` membership. The caller may only create the tenant their token is scoped
   * to (the slug is matched against the token's organization claims).
   */
  create(request: CreateOrganizationRequest): Promise<OrganizationResponse> {
    return this.http.send<OrganizationResponse>({
      method: "POST",
      path: "/organizations",
      body: request,
    });
  }

  /**
   * `DELETE /api/v1/organizations/{organizationSlug}` — delete a tenant
   * (offboarding/data deletion), cascading its workspaces, sessions, participants
   * and memberships. Owner-only (a non-Owner member is `403`); the offboarding is
   * recorded as a platform-level `OrganizationDeleted` audit fact that survives the
   * teardown. Responds `204 No Content`.
   */
  delete(organizationSlug: string): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/organizations/${encodeURIComponent(organizationSlug)}`,
    });
  }

  /**
   * `DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}` — remove
   * an organization member, revoking their access. The last Owner cannot be
   * removed. Audited. Responds `204 No Content`.
   */
  removeMember(organizationSlug: string, memberId: Uuid): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/members/${encodeURIComponent(memberId)}`,
    });
  }

  /**
   * `DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data`
   * — erase a data subject's personal data (GDPR Art.17): delete the user profile
   * and anonymize participant display data and invited-email rows, preserving a
   * PII-free audit chain. The sole org Owner cannot be erased (`409`). Audited.
   * Responds `204 No Content`.
   */
  eraseMemberPersonalData(
    organizationSlug: string,
    memberId: Uuid,
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/members/${encodeURIComponent(memberId)}/personal-data`,
    });
  }

  /**
   * `GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export`
   * — the data-subject access & portability export (GDPR Art.15/20): a
   * machine-readable export of the subject's personal data Core holds within the
   * tenant. Delivered only to the entitled recipient (the subject themselves or an
   * Owner/Admin acting on their behalf); any other tenant member is `403`. Audited;
   * never the invite token hash (threats T6/T7).
   */
  exportMemberPersonalData(
    organizationSlug: string,
    memberId: Uuid,
  ): Promise<PersonalDataExportResponse> {
    return this.http.send<PersonalDataExportResponse>({
      method: "GET",
      path: `/organizations/${encodeURIComponent(organizationSlug)}/members/${encodeURIComponent(memberId)}/personal-data-export`,
    });
  }
}
