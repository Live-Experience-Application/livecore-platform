// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
 * Rate-limit signals read from a response's `RateLimit-*` headers (CORE-DX-005).
 * Each field is present only when the matching header was set and parsed to a
 * non-negative integer. These are advisory ceilings/counts, never tenant or
 * principal detail (threat T7).
 */
export interface RateLimitInfo {
  /** The request quota (permit ceiling) for the current window (`RateLimit-Limit`). */
  readonly limit?: number;
  /** Permits remaining in the current window (`RateLimit-Remaining`; `0` on a `429`). */
  readonly remaining?: number;
  /** Seconds until the current window resets and the quota refills (`RateLimit-Reset`). */
  readonly reset?: number;
}

/**
 * The transport details parsed from a non-success response's headers, surfaced on
 * {@link LiveCoreApiError} so a caller can back off correctly instead of the SDK
 * discarding the response headers (CORE-DX-005).
 */
export interface ApiErrorDetails {
  /** Parsed `Retry-After` delay in seconds, when the header was present. */
  readonly retryAfter?: number;
  /** Parsed `RateLimit-*` signals, when any were present. */
  readonly rateLimit?: RateLimitInfo;
}

/**
 * A non-success HTTP response from the Core API. It carries the HTTP
 * {@link status} and, when the body was RFC 7807 Problem Details, the parsed
 * {@link problem}. On a throttled (`429`) or otherwise rate-limited response it
 * also surfaces the {@link retryAfter} delay and the {@link rateLimit} window
 * info, parsed from the response headers the Core API exposes (CORE-DX-005), so a
 * caller can honor the server's back-off rather than guess.
 *
 * A `404` may be a genuine not-found OR an intentionally hidden resource (a
 * fail-closed denial), so a caller MUST NOT infer existence from it
 * (docs/08_API_CONTRACTS.md). Problem Details and the rate-limit headers never
 * carry sensitive or hidden content (threat T7), so they are safe to surface to a
 * user; the SDK deliberately keeps the access token and request body out of this
 * error.
 */
export class LiveCoreApiError extends LiveCoreError {
  /** The HTTP status code of the failed response. */
  readonly status: number;
  /** The parsed Problem Details body, or `undefined` when the body was not one. */
  readonly problem?: ProblemDetails;
  /**
   * Seconds to wait before retrying, from the `Retry-After` response header, or
   * `undefined` when the response carried none (CORE-DX-005).
   */
  readonly retryAfter?: number;
  /**
   * Rate-limit window info from the `RateLimit-*` response headers, or `undefined`
   * when the response carried none (CORE-DX-005).
   */
  readonly rateLimit?: RateLimitInfo;

  constructor(
    status: number,
    problem?: ProblemDetails,
    details?: ApiErrorDetails,
  ) {
    super(LiveCoreApiError.composeMessage(status, problem));
    this.name = "LiveCoreApiError";
    this.status = status;
    this.problem = problem;
    this.retryAfter = details?.retryAfter;
    this.rateLimit = details?.rateLimit;
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
