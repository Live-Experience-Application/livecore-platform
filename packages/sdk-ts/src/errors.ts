/**
 * Error types the LiveCore SDK raises (CORE-SDK-002).
 *
 * A client-side failure (no access token, missing transport, unreadable body)
 * is a {@link LiveCoreError}; a non-success HTTP response is the
 * {@link LiveCoreApiError} subclass. Neither error embeds the access token or
 * the request body, so a thrown error can be logged without leaking a secret
 * (threats T4/T7 in docs/07_SECURITY_THREAT_MODEL.md).
 */
import type { ProblemDetails } from "@livecore/contracts";

/**
 * Base class for every error the SDK raises. Carries no API status; that is the
 * {@link LiveCoreApiError} subclass.
 */
export class LiveCoreError extends Error {
  constructor(message: string, options?: ErrorOptions) {
    super(message, options);
    this.name = "LiveCoreError";
  }
}

/**
 * A non-success HTTP response from the Core API. It carries the HTTP
 * {@link status} and, when the body was RFC 7807 Problem Details, the parsed
 * {@link problem}.
 *
 * A `404` may be a genuine not-found OR an intentionally hidden resource (a
 * fail-closed denial), so a caller MUST NOT infer existence from it
 * (docs/08_API_CONTRACTS.md). Problem Details never carry sensitive or hidden
 * content (threat T7), so {@link problem} is safe to surface to a user; the SDK
 * deliberately keeps the access token and request body out of this error.
 */
export class LiveCoreApiError extends LiveCoreError {
  /** The HTTP status code of the failed response. */
  readonly status: number;
  /** The parsed Problem Details body, or `undefined` when the body was not one. */
  readonly problem?: ProblemDetails;

  constructor(status: number, problem?: ProblemDetails) {
    super(LiveCoreApiError.composeMessage(status, problem));
    this.name = "LiveCoreApiError";
    this.status = status;
    this.problem = problem;
  }

  private static composeMessage(
    status: number,
    problem?: ProblemDetails,
  ): string {
    const title =
      typeof problem?.title === "string" && problem.title.length > 0
        ? problem.title
        : "Core API request failed";
    const detail =
      typeof problem?.detail === "string" && problem.detail.length > 0
        ? `: ${problem.detail}`
        : "";
    return `${title} (HTTP ${status})${detail}`;
  }
}

/** Narrows an unknown value to a {@link LiveCoreApiError}. */
export function isLiveCoreApiError(value: unknown): value is LiveCoreApiError {
  return value instanceof LiveCoreApiError;
}
