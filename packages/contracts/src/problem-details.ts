/**
 * RFC 7807 Problem Details — the error body every Core API endpoint returns on
 * failure (docs/08_API_CONTRACTS.md "Use Problem Details for errors";
 * docs/02_ARCHITECTURE.md "Error format"). Error bodies never leak sensitive or
 * hidden content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), so a vertical
 * app can surface `title`/`detail` to a user safely.
 */
export interface ProblemDetails {
  /** A URI reference identifying the problem type, or `"about:blank"`. */
  type?: string;
  /** A short, human-readable summary of the problem type. */
  title?: string;
  /** The HTTP status code for this occurrence. */
  status?: number;
  /** A human-readable explanation specific to this occurrence. */
  detail?: string;
  /** A URI reference identifying the specific occurrence. */
  instance?: string;
  /** RFC 7807 permits additional members; readers must tolerate them. */
  [extension: string]: unknown;
}

/**
 * HTTP status codes the Core API uses to signal errors
 * (docs/08_API_CONTRACTS.md "Common error codes"). A `404` may be a genuine
 * not-found or an intentionally hidden resource (a fail-closed denial), so a
 * client must not infer existence from it.
 */
export const CoreErrorStatusCodes = [
  400, 401, 403, 404, 409, 422, 429, 500,
] as const;

/** An HTTP status code the Core API returns on error. */
export type CoreErrorStatusCode = (typeof CoreErrorStatusCodes)[number];
