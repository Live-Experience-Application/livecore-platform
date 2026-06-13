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
} as const;

/** A standard Core API request header name. */
export type RequestHeader =
  (typeof RequestHeaders)[keyof typeof RequestHeaders];
