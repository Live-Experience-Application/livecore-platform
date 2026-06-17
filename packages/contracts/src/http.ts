// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Transport-level constants shared by every Core API contract (CORE-SDK-001).
 *
 * The Core API is versioned and served as JSON over HTTPS; errors use RFC 7807
 * Problem Details (docs/08_API_CONTRACTS.md). These constants let a vertical app
 * build request URLs and read the standard headers without hard-coding strings.
 */

/** Base path every versioned Core API route is mounted under. */
export const API_BASE_PATH = "/api/v1";

/**
 * Standard request headers the Core API reads
 * (docs/08_API_CONTRACTS.md "Common headers").
 */
export const RequestHeaders = {
  /** Bearer access token issued by the configured OIDC provider. */
  Authorization: "Authorization",
  /** Optional client-generated correlation id echoed in logs. */
  RequestId: "X-Request-Id",
  /**
   * Required on idempotent write commands (for example the reveal command) so a
   * client retry never produces a duplicate effect (docs/08_API_CONTRACTS.md
   * "Idempotency").
   */
  IdempotencyKey: "Idempotency-Key",
  /**
   * Optional conditional-write precondition on a mutating route: the weak `ETag`
   * (or its bare {@link ResponseHeaders.ETag} value) the caller last read for the
   * resource. A stale value is refused with `412` BEFORE the write, so a
   * GET-then-PUT across HTTP cannot silently clobber a concurrent change; an absent
   * value preserves the unconditional behavior (CORE-DX-002).
   */
  IfMatch: "If-Match",
} as const;

/** A standard Core API request header name. */
export type RequestHeader =
  (typeof RequestHeaders)[keyof typeof RequestHeaders];

/**
 * Standard response headers the Core API sets (docs/08_API_CONTRACTS.md).
 *
 * A browser/PWA SDK on a different origin can only READ a response header the CORS
 * policy explicitly exposes; the Core API exposes exactly this set
 * (`Access-Control-Expose-Headers`, CORE-DX-005 / CORE-DX-006), so every header named
 * here is readable cross-origin. None carries tenant/principal content — they are a
 * version validator, rate-limit signals, a created-resource path, a correlation id and
 * a deprecated route's own retirement schedule.
 */
export const ResponseHeaders = {
  /**
   * Weak entity-tag carrying a mutable resource's optimistic-concurrency version on
   * a single-resource read or mutation response. Echo it back as
   * {@link RequestHeaders.IfMatch} on a later write to make that write conditional
   * (CORE-DX-002).
   */
  ETag: "ETag",
  /**
   * `Location` of a freshly created resource on a `201 Created` response (for
   * example the new session's `/api/v1/sessions/{id}` path).
   */
  Location: "Location",
  /**
   * Seconds to wait before retrying, set on a `429 Too Many Requests` (and any
   * other throttled) response (CORE-SEC-001). The SDK surfaces it as
   * `LiveCoreApiError.retryAfter`.
   */
  RetryAfter: "Retry-After",
  /**
   * Per-request correlation/trace id the server returns so a consumer can correlate
   * a failed call with server logs/traces (CORE-OBS-005). Mirrors the optional
   * {@link RequestHeaders.RequestId} a client may send.
   */
  RequestId: "X-Request-Id",
  /**
   * The request quota (permit ceiling) for the caller's current rate-limit window —
   * the IETF draft `RateLimit` header family (CORE-DX-005). Emitted on an admitted
   * response and on a `429`. The SDK surfaces it as `LiveCoreApiError.rateLimit.limit`.
   */
  RateLimitLimit: "RateLimit-Limit",
  /** Permits remaining in the caller's current window (`0` on a `429`). */
  RateLimitRemaining: "RateLimit-Remaining",
  /** Seconds until the caller's current window resets and the quota refills. */
  RateLimitReset: "RateLimit-Reset",
  /**
   * Present on a response from a DEPRECATED route (CORE-DX-006): the route is on its
   * way out. The value is the boolean token `true`, or — when a specific date is
   * known — the IMF-fixdate the deprecation took effect. Pairs with
   * {@link ResponseHeaders.Sunset}. A current (non-deprecated) route sends neither.
   */
  Deprecation: "Deprecation",
  /**
   * The RFC 8594 retirement date of a deprecated route as an IMF-fixdate (CORE-DX-006):
   * the instant the route is expected to stop responding, so a consumer gets advance
   * signal before the contract changes. Sent together with
   * {@link ResponseHeaders.Deprecation}.
   */
  Sunset: "Sunset",
} as const;

/** A standard Core API response header name. */
export type ResponseHeader =
  (typeof ResponseHeaders)[keyof typeof ResponseHeaders];
