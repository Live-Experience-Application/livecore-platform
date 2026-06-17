// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Bounded-list pagination contracts (CORE-DX-003).
 *
 * Every Core list endpoint is bounded and returns a stable `items + hasMore` page
 * envelope rather than an unbounded array, modelled on the audit-log read
 * (docs/08_API_CONTRACTS.md "Pagination"; threat T9 in
 * docs/07_SECURITY_THREAT_MODEL.md). A consumer pages by the optional `limit` and
 * `offset` query parameters and reads {@link PageResponse.hasMore} to decide whether
 * to request the next page; the server clamps `limit` to a documented maximum so a
 * single read can never materialize a whole table.
 */

/** Query parameter names a consumer uses to page a bounded list endpoint. */
export const PageQuery = {
  /**
   * Optional page size. Defaults to {@link PAGE_DEFAULT_LIMIT} and is server-clamped
   * to {@link PAGE_MAX_LIMIT}; a present-but-malformed value (not a positive integer)
   * is a `400`.
   */
  Limit: "limit",
  /**
   * Optional zero-based start of the page. Defaults to `0`; a present-but-malformed
   * value (not a non-negative integer) is a `400`. Request the next page at
   * `offset = offset + items.length`.
   */
  Offset: "offset",
} as const;

/** A bounded-list paging query parameter name. */
export type PageQueryParam = (typeof PageQuery)[keyof typeof PageQuery];

/** Default page size when a request supplies no `limit` (matches the server). */
export const PAGE_DEFAULT_LIMIT = 50;

/**
 * Server-enforced maximum page size. A request for a larger `limit` is clamped to
 * this (never rejected), so a single list read can never return an unbounded array.
 */
export const PAGE_MAX_LIMIT = 200;

/**
 * A stable `items + hasMore` page of a bounded list endpoint (CORE-DX-003). At most
 * {@link limit} items starting at {@link offset}, with {@link hasMore} telling the
 * client whether a further page exists. The page items keep the endpoint's existing
 * per-item shape, including any role projection; only the SET is bounded.
 */
export interface PageResponse<T> {
  /** The zero-based index of the first item in this page within the full result set. */
  offset: number;
  /** The effective (server-clamped) maximum number of items this page may contain. */
  limit: number;
  /** Whether at least one further item exists after this page (request it at the next offset). */
  hasMore: boolean;
  /** The page of items, in the endpoint's deterministic order; at most {@link limit} of them. */
  items: T[];
}
