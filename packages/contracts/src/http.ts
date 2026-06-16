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
 */
export const ResponseHeaders = {
  /**
   * Weak entity-tag carrying a mutable resource's optimistic-concurrency version on
   * a single-resource read or mutation response. Echo it back as
   * {@link RequestHeaders.IfMatch} on a later write to make that write conditional
   * (CORE-DX-002).
   */
  ETag: "ETag",
} as const;

/** A standard Core API response header name. */
export type ResponseHeader =
  (typeof ResponseHeaders)[keyof typeof ResponseHeaders];
