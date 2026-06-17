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

/**
 * The minimal SignalR-style hub connection the live realtime client drives
 * (CORE-RT-007). A `@microsoft/signalr` `HubConnection` satisfies it, so the SDK
 * needs no SignalR dependency of its own — the same injectable-transport seam the
 * REST path uses for `fetch`. The SDK only registers the single server-fixed
 * client-method handler and starts/stops the connection; everything else (transport
 * negotiation, automatic reconnect) is the concrete connection's concern.
 */
export interface HubConnectionLike {
  /**
   * Registers a handler for a server-invoked client method. The SDK registers
   * exactly one, for the `SessionEvent` method; the delivered argument is a
   * `SessionEventReplayItem`-shaped envelope.
   */
  on(methodName: string, handler: (...args: unknown[]) => void): void;
  /** Removes a previously registered handler for the client method. */
  off(methodName: string, handler?: (...args: unknown[]) => void): void;
  /** Opens the connection. Rejects if the connection cannot be established. */
  start(): Promise<void>;
  /** Closes the connection and releases its resources. */
  stop(): Promise<void>;
}

/**
 * What the SDK hands a {@link HubConnectionFactory} to build a live hub connection
 * (CORE-RT-007). The SDK composes the hub {@link url} from the server-owned hub path
 * and the caller's connection identifiers (never a group name), and supplies an
 * {@link accessTokenFactory} the consumer wires to the SignalR client's
 * `accessTokenFactory` so the bearer token travels on the `access_token` query
 * parameter and is refreshed on each (re)connect.
 */
export interface HubConnectionRequest {
  /**
   * The fully composed hub URL: the API origin, the server-owned hub path and the
   * identifier query parameters. It carries NO access token — that is supplied
   * separately via {@link accessTokenFactory} so the secret never sits in a URL the
   * SDK composed.
   */
  readonly url: string;
  /**
   * Yields the bearer access token for the connection and every reconnect. The SDK
   * has already verified a token is available before the factory is called (fail
   * closed); wire this to the SignalR client's `accessTokenFactory`.
   */
  readonly accessTokenFactory: () => Promise<string>;
}

/**
 * Builds a {@link HubConnectionLike} for a composed {@link HubConnectionRequest}
 * (CORE-RT-007). A caller supplies one (typically wrapping a
 * `@microsoft/signalr` `HubConnectionBuilder().withUrl(url, { accessTokenFactory })`)
 * to enable the live realtime client; without one, opening a live connection fails
 * closed with a `LiveCoreError`.
 */
export type HubConnectionFactory = (
  request: HubConnectionRequest,
) => HubConnectionLike;

/**
 * A started live realtime connection (CORE-RT-007). The handle the live client
 * returns from a successful connect; call {@link stop} to close it.
 */
export interface LiveSessionConnection {
  /** Closes the live connection and releases its resources. */
  stop(): Promise<void>;
}

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
  /**
   * Builds the SignalR-style hub connection the live realtime client drives
   * (CORE-RT-007). Supply one — typically wrapping `@microsoft/signalr` — to open a
   * live session stream with `client.realtime.connect(...)`; the REST routes never
   * need it. Omitting it keeps the SDK free of a SignalR dependency, and opening a
   * live connection without one fails closed with a `LiveCoreError`.
   */
  hubConnectionFactory?: HubConnectionFactory;
}
