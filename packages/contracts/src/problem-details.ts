// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
  /**
   * A stable, machine-readable error code drawn from the {@link ProblemCodes}
   * catalog (CORE-DX-001). Branch on this code, never on the human `title`/`detail`
   * prose: the prose is free to change, the code is a contract. Every Core API error
   * carries one. Distinct conditions that share an HTTP status are still distinct
   * codes — for example the three `409`s (`quota_exceeded`, `workspace_archived`,
   * `concurrency_conflict`) are told apart by `code`, not by status.
   */
  code?: ProblemCode;
  /** RFC 7807 permits additional members; readers must tolerate them. */
  [extension: string]: unknown;
}

/**
 * The stable, documented catalog of machine-readable error codes the Core API
 * carries in the `code` member of every Problem Details response (CORE-DX-001).
 *
 * This list mirrors the server-side catalog exactly (a contract test asserts the
 * two never drift). The values are lower_snake_case and stable: a consumer may
 * persist them and branch on them. Several codes share an HTTP status — they are
 * deliberately distinct so a consumer can react to each correctly (notably the
 * three `409`s `quota_exceeded`, `workspace_archived` and `concurrency_conflict`).
 */
export const ProblemCodes = [
  /** The request was malformed or failed input validation (HTTP 400). */
  "validation_error",
  /** Authentication is missing or invalid (HTTP 401). */
  "authentication_required",
  /** Authenticated but not authorized for this action (HTTP 403). */
  "permission_denied",
  /**
   * Not found, or intentionally hidden as a fail-closed denial (HTTP 404). A
   * consumer must not infer existence from this code.
   */
  "not_found",
  /** A generic state conflict: the command is not legal from the current state (HTTP 409). */
  "conflict",
  /** A uniqueness constraint would be violated; the key/slug already exists (HTTP 409). */
  "duplicate_resource",
  /** The command would exceed a server-enforced quota; may change as usage frees up (HTTP 409). */
  "quota_exceeded",
  /** The target workspace is archived and therefore read-only (HTTP 409). */
  "workspace_archived",
  /** An optimistic-concurrency check failed; reload and retry (HTTP 409). */
  "concurrency_conflict",
  /**
   * An `If-Match` precondition failed: the caller's last-known weak `ETag` no longer
   * matches the resource's current version, so a conditional write is refused before it
   * runs; reload and retry (HTTP 412; CORE-DX-002).
   */
  "precondition_failed",
  /** The command was well-formed but is semantically invalid (HTTP 422). */
  "unprocessable_entity",
  /** The request body exceeds the accepted size cap (HTTP 413). */
  "payload_too_large",
  /** The request was rejected because a rate limit was exceeded (HTTP 429). */
  "rate_limited",
  /** An unexpected server error occurred; no internal detail is leaked (HTTP 500). */
  "internal_error",
  /** A required dependency (for example persistence) is not configured (HTTP 503). */
  "service_unavailable",
] as const;

/** A stable machine-readable Core API error code (a member of {@link ProblemCodes}). */
export type ProblemCode = (typeof ProblemCodes)[number];

/**
 * HTTP status codes the Core API uses to signal errors
 * (docs/08_API_CONTRACTS.md "Common error codes"). A `404` may be a genuine
 * not-found or an intentionally hidden resource (a fail-closed denial), so a
 * client must not infer existence from it.
 */
export const CoreErrorStatusCodes = [
  400, 401, 403, 404, 409, 412, 422, 429, 500,
] as const;

/** An HTTP status code the Core API returns on error. */
export type CoreErrorStatusCode = (typeof CoreErrorStatusCodes)[number];
