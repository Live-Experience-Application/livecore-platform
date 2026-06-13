/**
 * Configuration and transport-seam types for the LiveCore SDK client
 * (CORE-SDK-002).
 *
 * The SDK is OIDC-first: it never holds a password and never mints a token. The
 * caller supplies an {@link AccessTokenProvider} that yields a bearer access
 * token issued by the configured OIDC provider
 * (docs/adr/0005-oidc-first-authentication.md); the client attaches it to every
 * request. Server-side authorization remains the only authority — the SDK is a
 * typed transport, never a security boundary (docs/07_SECURITY_THREAT_MODEL.md).
 */

/**
 * Yields the bearer access token to send on the next request. May be async (for
 * example to refresh an expired token). Returning an empty value makes the
 * client fail closed: it raises a `LiveCoreError` and sends no request, so an
 * authenticated route is never called without a token.
 */
export type AccessTokenProvider = () => string | Promise<string>;

/** The subset of a standard `fetch` `Response` the SDK reads. */
export interface FetchResponse {
  /** Whether the response status is in the 2xx range. */
  readonly ok: boolean;
  /** The HTTP status code. */
  readonly status: number;
  /** Response headers; only `get` is used. */
  readonly headers: { get(name: string): string | null };
  /** The raw response body as text (the SDK parses JSON itself). */
  text(): Promise<string>;
}

/** The request init the SDK passes to its `fetch` implementation. */
export interface FetchRequestInit {
  /** The HTTP method. */
  method: string;
  /** The request headers the SDK composed. */
  headers: Record<string, string>;
  /** The JSON request body, already serialized, or absent for a bodyless request. */
  body?: string;
}

/**
 * The `fetch`-shaped function the SDK uses for transport. The global `fetch`
 * (Node 22+, browsers) satisfies it; a caller may inject one for testing or to
 * route through a custom transport.
 */
export type FetchLike = (
  url: string,
  init: FetchRequestInit,
) => Promise<FetchResponse>;

/** Options for constructing a `LiveCoreClient`. */
export interface LiveCoreClientOptions {
  /**
   * Absolute base URL of the Core API origin, WITHOUT the `/api/v1` version
   * prefix (the SDK appends it). Use HTTPS: the bearer token and any signed URL
   * are secrets in transit (threats T4/T7).
   */
  baseUrl: string;
  /** Supplies the OIDC bearer token for each request. */
  getAccessToken: AccessTokenProvider;
  /**
   * Transport implementation. Defaults to the global `fetch` when one is
   * available; an explicit value is required in a runtime without it.
   */
  fetch?: FetchLike;
  /**
   * Optional correlation-id generator. When set, its non-empty value is sent as
   * the `X-Request-Id` header so a call can be traced in server logs.
   */
  generateRequestId?: () => string;
  /**
   * Extra headers added to every request (for example a deployment routing
   * header). They never override the headers the SDK sets itself
   * (`Authorization`, `Accept`, `Content-Type`, `Idempotency-Key`).
   */
  defaultHeaders?: Record<string, string>;
}
