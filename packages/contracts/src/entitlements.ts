import type {
  AdEligibilityReasonCode,
  EntitlementValueKind,
  QuotaUnit,
} from "./enums.js";
import type { IsoDateTimeString } from "./scalars.js";

/**
 * Entitlements module contracts (CORE-SDK-001): the server-calculated quota
 * status, effective entitlements and ad-eligibility responses a mobile paywall or
 * feature guard consumes. The premium/ad-free state comes only from the server —
 * never trust a client-side flag (docs/21, docs/22).
 */

/** One quota's server-calculated status. */
export interface QuotaStatusItem {
  /** The generic, lower-case dotted quota key (e.g. `workspace.active.max`). */
  key: string;
  /** The unit the usage and limit are expressed in (`Count` or `Bytes`). */
  unit: QuotaUnit;
  /** The subject's recorded usage (non-negative), in {@link unit}. */
  used: number;
  /** The granted numeric cap, or `null` for an unlimited (fair-use) grant. */
  limit: number | null;
  /** Whether the subject holds an unlimited (fair-use) quota. */
  unlimited: boolean;
  /** Whether the subject holds an active quota entitlement for the key at all. */
  entitled: boolean;
  /** The headroom left (never below zero), or `null` for an unlimited grant. */
  remaining: number | null;
  /** Whether the recorded usage is over the limit. */
  exceeded: boolean;
}

/**
 * Response of `GET /api/v1/me/quota-status` and
 * `GET /api/v1/workspaces/{workspaceId}/quota-status`.
 */
export interface QuotaStatusResponse {
  /** The subject's server-calculated quota statuses, ordered by quota key. */
  quotas: QuotaStatusItem[];
}

/**
 * One resolved entitlement in the effective-entitlements response (CORE-SDK-006):
 * the generic entitlement key, its value kind and the granted value — a boolean for
 * a flag, a numeric cap for a quota (`null` meaning unlimited/fair-use). A paywall
 * or feature guard consumes it directly; the premium state comes only from the
 * server (docs/21).
 */
export interface EntitlementItem {
  /** The generic, lower-case dotted entitlement key (e.g. `ads.disabled`). */
  key: string;
  /** Whether the entitlement is a boolean flag or a numeric quota. */
  valueKind: EntitlementValueKind;
  /** The granted boolean value for a flag entitlement; `null` for a quota. */
  flagValue: boolean | null;
  /**
   * The granted numeric cap for a quota entitlement — `null` meaning unlimited
   * (fair-use); `null` for a flag.
   */
  quotaLimit: number | null;
}

/**
 * Response of `GET /api/v1/me/entitlements`: the current user's server-authoritative
 * effective entitlements, ordered by the generic entitlement key. Empty when the
 * subject holds no active entitlements (the fail-closed default).
 */
export interface MeEntitlementsResponse {
  /** The subject's resolved effective entitlements, ordered by key. */
  entitlements: EntitlementItem[];
}

/** Response of `GET /api/v1/me/ad-eligibility`. */
export interface AdEligibilityResponse {
  /** Whether the subject must be shown ads (fail-closed default `true`). */
  adsRequired: boolean;
  /** The stable, generic wire code explaining the decision. */
  reason: AdEligibilityReasonCode;
  /** The temporary per-session ad-free expiry, or `null` when none. */
  sessionAdFreeUntil: IsoDateTimeString | null;
  /** Whether sessions hosted by the subject are ad-free for participants. */
  hostedSessionAdFree: boolean;
}
