import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Organizations module contracts (CORE-SDK-006). The organization is the
 * Core-owned, product-neutral tenant root (docs/03_DOMAIN_LANGUAGE.md). These
 * generic DTOs back the organization list/create routes and the tenant-offboarding
 * delete (csv/api_routes.csv); they carry only generic Core fields and no internal
 * authorization rationale (docs/08_API_CONTRACTS.md).
 */

/**
 * Request body for `POST /api/v1/organizations`. The new tenant names itself by
 * its own {@link slug} and display {@link name}; unlike a workspace create it
 * carries no parent-organization field, because the organization IS the boundary
 * being created. The caller may only create the tenant their token is scoped to.
 */
export interface CreateOrganizationRequest {
  /** Globally-unique natural key of the new organization (lower-case, URL-safe). */
  slug: string;
  /** Human-readable display name of the organization. */
  name: string;
}

/**
 * Response projection of an organization. It carries no membership/role detail —
 * the caller's principal context (memberships and roles) is the separate
 * `GET /api/v1/me` projection ({@link CurrentPrincipalResponse}).
 */
export interface OrganizationResponse {
  /** Surrogate id of the organization, the tenant root. */
  id: Uuid;
  /** Globally-unique natural key of the organization. */
  slug: string;
  /** Human-readable display name. */
  name: string;
  /** When the organization was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the organization was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
