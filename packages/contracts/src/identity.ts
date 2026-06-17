import type { MembershipRole } from "./enums.js";
import type { Uuid } from "./scalars.js";

/**
 * Identity & access module contracts (CORE-SDK-006): the principal-context
 * projection returned by `GET /api/v1/me` (csv/api_routes.csv "Returns principal
 * context only"). It is the safe, generic answer to "who am I, and what can I act
 * as?": the caller's own user profile plus the organization memberships the caller
 * holds and the role in each. It carries identifiers and the caller's own display
 * metadata only — never the access token or any other secret (threat T7).
 */

/**
 * The caller's own user profile: the surrogate profile id, the OIDC identity pair
 * (issuer + subject) that keys it, and the optional display metadata mirrored from
 * the provider. This is the caller's OWN data only; it never projects another user.
 */
export interface CurrentUserResponse {
  /** Surrogate id of the caller's user profile. */
  id: Uuid;
  /** Identity of the OIDC provider that asserted the caller. */
  issuer: string;
  /** Issuer-local stable identifier of the caller. */
  subject: string;
  /** Optional human-readable name mirrored from the provider. */
  displayName: string | null;
  /** Optional email address mirrored from the provider. */
  email: string | null;
}

/**
 * One of the caller's organization memberships: the organization the caller
 * belongs to and the generic role the caller holds in it. The membership list is
 * filtered to the tenants the token claims, so it never exposes a tenant the token
 * does not assert (threat T5).
 */
export interface OrganizationMembershipResponse {
  /** Surrogate id of the organization. */
  organizationId: Uuid;
  /** Globally-unique natural key of the organization. */
  organizationSlug: string;
  /** Human-readable display name of the organization. */
  organizationName: string;
  /** The generic role the caller holds in the organization. */
  role: MembershipRole;
}

/** Response projection of the authenticated caller's principal context. */
export interface CurrentPrincipalResponse {
  /** The caller's own user profile. */
  user: CurrentUserResponse;
  /** The caller's organization memberships (organization + role), token-filtered. */
  memberships: OrganizationMembershipResponse[];
}
