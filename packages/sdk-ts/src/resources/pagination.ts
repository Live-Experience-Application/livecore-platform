/**
 * Shared paging-parameter helper for the bounded SDK list methods (CORE-DX-003).
 *
 * Every Core list endpoint accepts an optional `limit`/`offset` and returns a
 * `PageResponse<T>` envelope (docs/08_API_CONTRACTS.md "Pagination"). This turns the
 * optional numeric {@link PageParams} a caller passes into the string query the
 * transport sends; an omitted value is left off the URL so the server applies its
 * default (limit 50) and clamps an oversized limit to its maximum (200).
 */

/** Optional bounded-list paging parameters accepted by every SDK list method. */
export interface PageParams {
  /** Page size; defaults to 50 server-side and is clamped to a maximum of 200. */
  limit?: number;
  /** Zero-based start of the page; defaults to 0. */
  offset?: number;
}

/**
 * Builds the `limit`/`offset` query fragment from {@link PageParams}, omitting any
 * value the caller did not supply (so the server applies its defaults). The values
 * are serialized as strings for the transport's query map.
 */
export function pageQuery(params?: PageParams): {
  limit?: string;
  offset?: string;
} {
  return {
    limit: params?.limit === undefined ? undefined : String(params.limit),
    offset: params?.offset === undefined ? undefined : String(params.offset),
  };
}
