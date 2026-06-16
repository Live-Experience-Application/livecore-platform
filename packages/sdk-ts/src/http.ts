/**
 * The internal HTTP transport shared by every SDK resource group (CORE-SDK-002).
 *
 * It composes one Core API request from a {@link RequestSpec}, attaches the OIDC
 * bearer token (failing closed when none is available so an authenticated route
 * is never called unauthenticated), sends it through the configured `fetch`, and
 * turns a non-success response into a `LiveCoreApiError`. It is not part of the
 * public surface; callers use the typed resource methods on `LiveCoreClient`.
 */
import { API_BASE_PATH } from "@livecore/contracts";
import type { ProblemDetails } from "@livecore/contracts";

import { LiveCoreApiError, LiveCoreError } from "./errors.js";
import type {
  AccessTokenProvider,
  FetchLike,
  FetchRequestInit,
  FetchResponse,
  LiveCoreClientOptions,
} from "./options.js";

/** An internal description of one Core API request. */
export interface RequestSpec {
  /** The HTTP method. */
  method: "GET" | "POST" | "PUT" | "DELETE";
  /**
   * Path under {@link API_BASE_PATH}, beginning with `/`. Any interpolated id
   * must already be percent-encoded by the caller.
   */
  path: string;
  /** Query parameters; an `undefined` value is omitted from the URL. */
  query?: Record<string, string | undefined>;
  /** The JSON request body, serialized as-is; absent for a bodyless request. */
  body?: unknown;
  /**
   * Idempotency key for a retry-safe write command. Sent as the
   * `Idempotency-Key` header (docs/08_API_CONTRACTS.md "Idempotency"). The same
   * key MUST be reused across retries of one logical command.
   */
  idempotencyKey?: string;
  /**
   * Conditional-write precondition for a mutating route. Sent as the `If-Match`
   * header (CORE-DX-002): the weak `ETag` (or its bare value) the caller last read
   * for the resource. A stale value is refused with `412` before the write; an
   * absent value preserves the unconditional behavior.
   */
  ifMatch?: string;
}

/**
 * A response body together with its `ETag` (CORE-DX-002): the weak
 * optimistic-concurrency validator the Core API returns on a single-resource read
 * or mutation. Pass {@link etag} back as `ifMatch` on a later write to make that
 * write conditional, so a GET-then-PUT across HTTP cannot silently clobber a
 * concurrent change.
 */
export interface SdkResponse<TResponse> {
  /** The parsed JSON response body. */
  readonly data: TResponse;
  /** The weak `ETag` from the response, or `null` when the response carried none. */
  readonly etag: string | null;
}

/** Resolves the global `fetch`, or throws a clear error when none exists. */
function resolveGlobalFetch(): FetchLike {
  const candidate = (globalThis as unknown as { fetch?: unknown }).fetch;
  if (typeof candidate !== "function") {
    throw new LiveCoreError(
      "No global fetch is available; pass a fetch implementation in LiveCoreClientOptions.",
    );
  }
  return candidate as FetchLike;
}

/** Best-effort parse of an error body as RFC 7807 Problem Details. */
function parseProblemDetails(raw: string): ProblemDetails | undefined {
  if (raw.length === 0) {
    return undefined;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return undefined;
  }
  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return undefined;
  }
  return parsed as ProblemDetails;
}

/** Shared, authenticated JSON transport for the Core API. */
export class HttpClient {
  private readonly baseUrl: string;
  private readonly getAccessToken: AccessTokenProvider;
  private readonly fetchImpl: FetchLike;
  private readonly generateRequestId?: () => string;
  private readonly defaultHeaders: Record<string, string>;

  constructor(options: LiveCoreClientOptions) {
    if (typeof options?.baseUrl !== "string" || options.baseUrl.length === 0) {
      throw new LiveCoreError("LiveCoreClient requires a non-empty baseUrl.");
    }
    if (typeof options.getAccessToken !== "function") {
      throw new LiveCoreError(
        "LiveCoreClient requires a getAccessToken provider.",
      );
    }
    this.baseUrl = options.baseUrl.replace(/\/+$/, "");
    this.getAccessToken = options.getAccessToken;
    this.fetchImpl = options.fetch ?? resolveGlobalFetch();
    this.generateRequestId = options.generateRequestId;
    this.defaultHeaders = { ...(options.defaultHeaders ?? {}) };
  }

  /** Sends one request and returns its parsed JSON body typed as `TResponse`. */
  async send<TResponse>(spec: RequestSpec): Promise<TResponse> {
    const { data } = await this.sendWithETag<TResponse>(spec);
    return data;
  }

  /**
   * Sends one request and returns its parsed JSON body together with the response's
   * weak `ETag` (CORE-DX-002), so a caller can echo the tag back as `ifMatch` on a
   * later conditional write. The same transport, auth and error behavior as
   * {@link send}; a non-success response still raises a `LiveCoreApiError`.
   */
  async sendWithETag<TResponse>(
    spec: RequestSpec,
  ): Promise<SdkResponse<TResponse>> {
    const token = await this.resolveToken();
    const url = this.buildUrl(spec);
    const headers = this.buildHeaders(token, spec);

    const init: FetchRequestInit = { method: spec.method, headers };
    if (spec.body !== undefined) {
      init.body = JSON.stringify(spec.body);
    }

    let response: FetchResponse;
    try {
      response = await this.fetchImpl(url, init);
    } catch (cause) {
      throw new LiveCoreError("The Core API request could not be sent.", {
        cause,
      });
    }

    // Read the validator before consuming the body; header lookup is
    // case-insensitive per the WHATWG fetch Headers contract.
    const etag = response.headers.get("etag");
    const data = await this.readResponse<TResponse>(response);
    return { data, etag };
  }

  /** Fail closed: never send an authenticated request without a token. */
  private async resolveToken(): Promise<string> {
    const token = await this.getAccessToken();
    if (typeof token !== "string" || token.length === 0) {
      throw new LiveCoreError(
        "No access token was provided; refusing to send an unauthenticated request.",
      );
    }
    return token;
  }

  private buildUrl(spec: RequestSpec): string {
    let url = this.baseUrl + API_BASE_PATH + spec.path;
    if (spec.query) {
      const pairs: string[] = [];
      for (const [key, value] of Object.entries(spec.query)) {
        if (value !== undefined) {
          pairs.push(`${encodeURIComponent(key)}=${encodeURIComponent(value)}`);
        }
      }
      if (pairs.length > 0) {
        url += `?${pairs.join("&")}`;
      }
    }
    return url;
  }

  private buildHeaders(
    token: string,
    spec: RequestSpec,
  ): Record<string, string> {
    // The SDK's own headers are applied LAST so a caller's defaultHeaders can
    // never override the security-relevant Authorization/Idempotency headers.
    const headers: Record<string, string> = {
      ...this.defaultHeaders,
      Accept: "application/json",
      Authorization: `Bearer ${token}`,
    };
    if (spec.body !== undefined) {
      headers["Content-Type"] = "application/json";
    }
    if (spec.idempotencyKey !== undefined) {
      headers["Idempotency-Key"] = spec.idempotencyKey;
    }
    if (spec.ifMatch !== undefined) {
      headers["If-Match"] = spec.ifMatch;
    }
    if (this.generateRequestId) {
      const requestId = this.generateRequestId();
      if (typeof requestId === "string" && requestId.length > 0) {
        headers["X-Request-Id"] = requestId;
      }
    }
    return headers;
  }

  private async readResponse<TResponse>(
    response: FetchResponse,
  ): Promise<TResponse> {
    const raw = await response.text();
    if (!response.ok) {
      throw new LiveCoreApiError(response.status, parseProblemDetails(raw));
    }
    if (raw.length === 0) {
      return undefined as TResponse;
    }
    try {
      return JSON.parse(raw) as TResponse;
    } catch (cause) {
      throw new LiveCoreError(
        "The Core API response body was not valid JSON.",
        {
          cause,
        },
      );
    }
  }
}
