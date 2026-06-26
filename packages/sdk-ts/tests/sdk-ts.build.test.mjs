/**
 * Package build tests for @livecore/sdk-ts (CORE-SDK-002).
 *
 * These run with the Node built-in test runner (no new dependency) against the
 * COMPILED package output in `dist/`, so they fail if the package does not build
 * or does not expose its stable runtime surface. They exercise the transport
 * with an injected `fetch` (no network), asserting the security-relevant
 * behavior: the bearer token is attached to every request, a fresh idempotency
 * key is never invented, a denial surfaces as a typed error (never a success
 * value), the client fails closed when no token is available, and a secret is
 * never embedded in an error.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import {
  LiveCoreApiError,
  LiveCoreClient,
  LiveCoreError,
  PACKAGE_NAME,
  VERSION,
  isLiveCoreApiError,
} from "../dist/index.js";

const BASE_URL = "https://core.example.test";
const TOKEN = "test-access-token";

function jsonResponse(status, body) {
  const text = body === undefined ? "" : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => null },
    text: () => Promise.resolve(text),
  };
}

function makeClient(options = {}) {
  const calls = [];
  const fetchImpl = (url, init) => {
    calls.push({ url, init });
    const handler = options.handler ?? (() => jsonResponse(200, {}));
    return Promise.resolve(handler(url, init, calls.length - 1));
  };
  const client = new LiveCoreClient({
    baseUrl: BASE_URL,
    getAccessToken: options.getAccessToken ?? (() => TOKEN),
    fetch: fetchImpl,
    ...(options.generateRequestId
      ? { generateRequestId: options.generateRequestId }
      : {}),
    ...(options.defaultHeaders
      ? { defaultHeaders: options.defaultHeaders }
      : {}),
  });
  return { client, calls };
}

/**
 * Builds a client whose live realtime connections are driven by a FAKE
 * SignalR-style hub connection (CORE-RT-007), so the live client is exercised with
 * no network and no SignalR dependency. The fake records the composed hub request,
 * the registered handlers and the start/stop calls, and lets a test deliver an event
 * to the registered handler with `emit`.
 */
function makeHubHarness(options = {}) {
  const factoryCalls = [];
  const connections = [];
  const hubConnectionFactory = (request) => {
    factoryCalls.push(request);
    const handlers = new Map();
    const connection = {
      request,
      handlers,
      started: false,
      stopped: false,
      on(methodName, handler) {
        handlers.set(methodName, handler);
      },
      off(methodName) {
        handlers.delete(methodName);
      },
      start() {
        connection.started = true;
        return Promise.resolve();
      },
      stop() {
        connection.stopped = true;
        return Promise.resolve();
      },
      emit(methodName, payload) {
        const handler = handlers.get(methodName);
        if (handler) {
          handler(payload);
        }
      },
    };
    connections.push(connection);
    return connection;
  };
  const client = new LiveCoreClient({
    baseUrl: BASE_URL,
    getAccessToken: options.getAccessToken ?? (() => TOKEN),
    fetch: () => Promise.resolve(jsonResponse(200, {})),
    ...(options.omitFactory ? {} : { hubConnectionFactory }),
  });
  return { client, factoryCalls, connections };
}

test("the built package exposes its stable name", () => {
  assert.equal(PACKAGE_NAME, "@livecore/sdk-ts");
});

test("create workspace POSTs the body with the bearer token", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(201, {
        id: "ws-1",
        organizationId: "org-1",
        slug: "demo",
        name: "Demo",
        createdAt: "2026-06-13T00:00:00+00:00",
        updatedAt: "2026-06-13T00:00:00+00:00",
      }),
  });

  const result = await client.workspaces.create({
    organizationSlug: "acme",
    slug: "demo",
    name: "Demo",
  });

  assert.equal(result.id, "ws-1");
  assert.equal(calls.length, 1);
  const { url, init } = calls[0];
  assert.equal(url, "https://core.example.test/api/v1/workspaces");
  assert.equal(init.method, "POST");
  assert.equal(init.headers.Authorization, "Bearer test-access-token");
  assert.equal(init.headers["Content-Type"], "application/json");
  assert.equal(init.headers.Accept, "application/json");
  assert.deepEqual(JSON.parse(init.body), {
    organizationSlug: "acme",
    slug: "demo",
    name: "Demo",
  });
});

test("list workspaces returns the bounded page envelope and sends only the org slug when unpaged", async () => {
  const page = { offset: 0, limit: 50, hasMore: false, items: [] };
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, page),
  });

  const result = await client.workspaces.list({ organizationSlug: "acme" });

  assert.deepEqual(result, page);
  const { url, init } = calls[0];
  // No limit/offset supplied, so neither is added to the URL (the server applies its defaults).
  assert.equal(
    url,
    "https://core.example.test/api/v1/workspaces?organizationSlug=acme",
  );
  assert.equal(init.method, "GET");
  assert.equal(init.body, undefined);
  assert.equal(init.headers["Content-Type"], undefined);
  assert.equal(init.headers.Authorization, "Bearer test-access-token");
});

test("list workspaces forwards the limit/offset paging parameters in the query (CORE-DX-003)", async () => {
  const page = { offset: 50, limit: 25, hasMore: true, items: [] };
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, page),
  });

  const result = await client.workspaces.list({
    organizationSlug: "acme",
    limit: 25,
    offset: 50,
  });

  assert.deepEqual(result, page);
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/workspaces?organizationSlug=acme&limit=25&offset=50",
  );
});

test("reveal sends the Idempotency-Key header and the org slug in the body", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(200, {
        resourceType: "Scene",
        resourceId: "sc-1",
        visible: true,
        outcome: "Applied",
        participantId: null,
      }),
  });

  const result = await client.visibility.reveal(
    "sess-1",
    { organizationSlug: "acme", resourceType: "Scene", resourceId: "sc-1" },
    { idempotencyKey: "key-123" },
  );

  assert.equal(result.outcome, "Applied");
  const { url, init } = calls[0];
  assert.equal(url, "https://core.example.test/api/v1/sessions/sess-1/reveal");
  assert.equal(init.headers["Idempotency-Key"], "key-123");
  assert.deepEqual(JSON.parse(init.body), {
    organizationSlug: "acme",
    resourceType: "Scene",
    resourceId: "sc-1",
  });
});

test("the weak ETag round-trips through the SDK as a conditional-write If-Match (CORE-DX-002)", async () => {
  const etag = 'W/"8147"';
  const { client, calls } = makeClient({
    handler: (_url, init) => {
      // The read carries the weak ETag; the conditional write echoes it back.
      if (init.method === "GET") {
        return {
          ok: true,
          status: 200,
          headers: {
            get: (name) => (name.toLowerCase() === "etag" ? etag : null),
          },
          text: () =>
            Promise.resolve(
              JSON.stringify({
                id: "ws-1",
                organizationId: "org-1",
                slug: "demo",
                name: "Demo",
                status: "Active",
                createdAt: "2026-06-13T00:00:00+00:00",
                updatedAt: "2026-06-13T00:00:00+00:00",
                version: "8147",
              }),
            ),
        };
      }
      return jsonResponse(200, {
        id: "ws-1",
        organizationId: "org-1",
        slug: "demo",
        name: "Renamed",
        status: "Active",
        createdAt: "2026-06-13T00:00:00+00:00",
        updatedAt: "2026-06-13T00:00:01+00:00",
        version: "8148",
      });
    },
  });

  // The read exposes the resource's weak ETag (and the same value on the body).
  const read = await client.workspaces.getWithETag("ws-1", {
    organizationSlug: "acme",
  });
  assert.equal(read.etag, etag);
  assert.equal(read.data.version, "8147");
  assert.equal(calls[0].init.headers["If-Match"], undefined);

  // Echoing it back as If-Match makes the rename conditional on that version.
  const renamed = await client.workspaces.update(
    "ws-1",
    { organizationSlug: "acme", name: "Renamed" },
    { ifMatch: read.etag ?? undefined },
  );
  assert.equal(renamed.version, "8148");
  assert.equal(calls[1].init.method, "PUT");
  assert.equal(calls[1].init.headers["If-Match"], etag);
});

test("a workspace rename omits If-Match when none is supplied (unconditional, current behavior)", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(200, {
        id: "ws-1",
        organizationId: "org-1",
        slug: "demo",
        name: "Renamed",
        status: "Active",
        createdAt: "2026-06-13T00:00:00+00:00",
        updatedAt: "2026-06-13T00:00:01+00:00",
        version: "8148",
      }),
  });

  await client.workspaces.update("ws-1", {
    organizationSlug: "acme",
    name: "Renamed",
  });

  assert.equal(calls[0].init.headers["If-Match"], undefined);
});

test("session event replay forwards optional query params and omits them when unset", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(200, {
        sessionId: "sess-1",
        events: [],
        generatedAt: "2026-06-13T00:00:00+00:00",
      }),
  });

  await client.realtime.getSessionEvents("sess-1", {
    organizationSlug: "acme",
    participantId: "p-1",
    afterSequence: 9,
  });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/sessions/sess-1/events?organizationSlug=acme&participantId=p-1&afterSequence=9",
  );

  await client.realtime.getSessionEvents("sess-1", {
    organizationSlug: "acme",
  });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/sessions/sess-1/events?organizationSlug=acme",
  );
});

// --- CORE-RT-007: the typed live realtime client over the SignalR hub. ----------
// The live client builds the correct hub URL/params, registers the single
// server-fixed client method as one handler, surfaces a typed event stream, and
// fails closed without an access token. Negative checks prove a client supplies only
// identifiers (never a group name), so it cannot select a group or another
// participant's feed.

test("the live client connects to the hub with identifier params and surfaces a typed event stream (CORE-RT-007)", async () => {
  const { client, factoryCalls, connections } = makeHubHarness();
  const received = [];

  const connection = await client.realtime.connect(
    { organizationSlug: "acme", sessionId: "sess-1", participantId: "p-1" },
    (event) => received.push(event),
  );

  // The hub URL is the origin + the server-owned /hubs/session path + the identifier
  // query params — NOT under the /api/v1 REST surface.
  assert.equal(factoryCalls.length, 1);
  assert.equal(
    factoryCalls[0].url,
    "https://core.example.test/hubs/session?organizationSlug=acme&sessionId=sess-1&participantId=p-1",
  );
  assert.ok(!factoryCalls[0].url.includes("/api/v1"));

  // The bearer token travels via the connection's accessTokenFactory (the access_token
  // query param SignalR appends), never baked into the URL the SDK composed (threat T7).
  assert.ok(!factoryCalls[0].url.includes("access_token"));
  assert.equal(await factoryCalls[0].accessTokenFactory(), TOKEN);

  // Exactly one handler, registered for the single server-fixed client method, and the
  // connection is started.
  assert.equal(connections.length, 1);
  assert.equal(connections[0].started, true);
  assert.deepEqual([...connections[0].handlers.keys()], ["SessionEvent"]);

  // A delivered envelope reaches the handler as the recipient-safe replay-item shape,
  // so a consumer routes live and replayed events through one handler.
  const envelope = {
    eventId: "0190f1d4-9b6e-7c3a-8a1e-0c2b3d4e5f60",
    sequence: 7,
    eventType: "ContentRevealed",
    sessionId: "sess-1",
    payload: '{"ResourceType":"Scene","ResourceId":"sc-1"}',
    schemaVersion: 1,
    createdAt: "2026-06-13T00:00:00+00:00",
    targetParticipantId: null,
  };
  connections[0].emit("SessionEvent", envelope);
  assert.deepEqual(received, [envelope]);

  // The returned handle stops the underlying connection.
  await connection.stop();
  assert.equal(connections[0].stopped, true);
});

test("a host live connection omits the participant id (the server resolves the host/observer groups)", async () => {
  const { client, factoryCalls } = makeHubHarness();

  await client.realtime.connect(
    { organizationSlug: "acme", sessionId: "sess-1" },
    () => {},
  );

  assert.equal(
    factoryCalls[0].url,
    "https://core.example.test/hubs/session?organizationSlug=acme&sessionId=sess-1",
  );
});

test("the live client supplies only identifiers — it cannot select a group or another participant's feed (CORE-RT-007)", async () => {
  const { client, factoryCalls } = makeHubHarness();

  await client.realtime.connect(
    { organizationSlug: "acme", sessionId: "sess-1", participantId: "p-1" },
    () => {},
  );

  const url = factoryCalls[0].url;
  // There is no group-name parameter on the URL — groups are server-managed, never
  // client-chosen (CORE-RT-002; threat T3).
  assert.ok(!/group/i.test(url));
  const query = new URL(url).searchParams;
  assert.deepEqual([...query.keys()].sort(), [
    "organizationSlug",
    "participantId",
    "sessionId",
  ]);
  // The participant id is the caller's own; there is no API to name another feed.
  assert.equal(query.get("participantId"), "p-1");
});

test("fail closed: a live connection without an access token is refused client-side and no hub connection is opened (CORE-RT-007)", async () => {
  const { client, factoryCalls, connections } = makeHubHarness({
    getAccessToken: () => "",
  });

  await assert.rejects(
    () =>
      client.realtime.connect(
        { organizationSlug: "acme", sessionId: "sess-1" },
        () => {},
      ),
    (error) => {
      assert.ok(error instanceof LiveCoreError);
      assert.ok(!(error instanceof LiveCoreApiError));
      return true;
    },
  );

  // The token is resolved BEFORE the factory is invoked, so no connection is built or
  // started — the fail-closed guard runs entirely client-side.
  assert.equal(factoryCalls.length, 0);
  assert.equal(connections.length, 0);
});

test("a live connection without a configured hubConnectionFactory fails closed", async () => {
  const { client } = makeHubHarness({ omitFactory: true });

  await assert.rejects(
    () =>
      client.realtime.connect(
        { organizationSlug: "acme", sessionId: "sess-1" },
        () => {},
      ),
    (error) => {
      assert.ok(error instanceof LiveCoreError);
      assert.ok(!(error instanceof LiveCoreApiError));
      return true;
    },
  );
});

test("fail closed: no access token means no request is sent", async () => {
  const { client, calls } = makeClient({ getAccessToken: () => "" });

  await assert.rejects(
    () => client.workspaces.list({ organizationSlug: "acme" }),
    (error) => {
      assert.ok(error instanceof LiveCoreError);
      assert.ok(!(error instanceof LiveCoreApiError));
      return true;
    },
  );
  assert.equal(calls.length, 0);
});

test("a 403 denial surfaces as a LiveCoreApiError, never a success value", async () => {
  const { client } = makeClient({
    handler: () =>
      jsonResponse(403, {
        type: "about:blank",
        title: "Forbidden",
        status: 403,
      }),
  });

  await assert.rejects(
    () =>
      client.workspaces.create({
        organizationSlug: "acme",
        slug: "demo",
        name: "Demo",
      }),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 403);
      assert.equal(error.problem?.title, "Forbidden");
      return true;
    },
  );
});

test("a 404 hidden resource surfaces as a LiveCoreApiError and is not treated as found", async () => {
  const { client } = makeClient({
    handler: () => jsonResponse(404, { title: "Not Found", status: 404 }),
  });

  await assert.rejects(
    () => client.workspaces.get("ws-x", { organizationSlug: "acme" }),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 404);
      return true;
    },
  );
});

test("an API error never embeds the bearer token", async () => {
  const { client } = makeClient({
    handler: () => jsonResponse(403, { title: "Forbidden", status: 403 }),
  });

  let captured;
  try {
    await client.workspaces.list({ organizationSlug: "acme" });
  } catch (error) {
    captured = error;
  }

  assert.ok(isLiveCoreApiError(captured));
  assert.ok(!String(captured.message).includes(TOKEN));
  assert.ok(!JSON.stringify(captured.problem ?? {}).includes(TOKEN));
});

test("defaultHeaders cannot override the Authorization the SDK sets", async () => {
  const { client, calls } = makeClient({
    defaultHeaders: { Authorization: "Bearer spoofed", "X-Tenant-Route": "eu" },
    handler: () => jsonResponse(200, []),
  });

  await client.workspaces.list({ organizationSlug: "acme" });

  assert.equal(calls[0].init.headers.Authorization, "Bearer test-access-token");
  assert.equal(calls[0].init.headers["X-Tenant-Route"], "eu");
});

test("a 429 surfaces retryAfter and rate-limit info on the error (CORE-DX-005)", async () => {
  // The Core API exposes Retry-After and the RateLimit-* headers on a throttled response; the SDK must
  // surface them on the error type instead of discarding the response headers, so a caller can back off.
  const headers = {
    "retry-after": "30",
    "ratelimit-limit": "300",
    "ratelimit-remaining": "0",
    "ratelimit-reset": "30",
  };
  const { client } = makeClient({
    handler: () => ({
      ok: false,
      status: 429,
      headers: { get: (name) => headers[name.toLowerCase()] ?? null },
      text: () =>
        Promise.resolve(
          JSON.stringify({
            title: "Too Many Requests",
            status: 429,
            code: "rate_limited",
          }),
        ),
    }),
  });

  let captured;
  try {
    await client.workspaces.list({ organizationSlug: "acme" });
  } catch (error) {
    captured = error;
  }

  assert.ok(isLiveCoreApiError(captured));
  assert.equal(captured.status, 429);
  assert.equal(captured.retryAfter, 30);
  assert.deepEqual(captured.rateLimit, { limit: 300, remaining: 0, reset: 30 });
  // The rate-limit signals leak nothing: only numeric ceilings/counts, never tenant or principal detail.
  assert.equal(captured.problem?.code, "rate_limited");
});

test("an error with no rate-limit headers leaves retryAfter and rateLimit undefined", async () => {
  const { client } = makeClient({
    handler: () => jsonResponse(403, { title: "Forbidden", status: 403 }),
  });

  let captured;
  try {
    await client.workspaces.create({
      organizationSlug: "acme",
      slug: "demo",
      name: "Demo",
    });
  } catch (error) {
    captured = error;
  }

  assert.ok(isLiveCoreApiError(captured));
  assert.equal(captured.retryAfter, undefined);
  assert.equal(captured.rateLimit, undefined);
});

test("a non-integer Retry-After (HTTP-date) is treated as absent, not guessed", async () => {
  const headers = { "retry-after": "Wed, 21 Oct 2026 07:28:00 GMT" };
  const { client } = makeClient({
    handler: () => ({
      ok: false,
      status: 503,
      headers: { get: (name) => headers[name.toLowerCase()] ?? null },
      text: () => Promise.resolve(""),
    }),
  });

  let captured;
  try {
    await client.workspaces.list({ organizationSlug: "acme" });
  } catch (error) {
    captured = error;
  }

  assert.ok(isLiveCoreApiError(captured));
  assert.equal(captured.retryAfter, undefined);
  assert.equal(captured.rateLimit, undefined);
});

// --- CORE-SDX-001: the echoed correlation ids surface on BOTH paths. ------------
// The Core API echoes X-Request-Id and the W3C traceparent on every response
// (CorrelationHeaderMiddleware, CORS-exposed). The SDK surfaces them on the success
// envelope and on the thrown LiveCoreApiError, so a consumer logs request_id with
// every call and shows it in an error state without wrapping the injected fetch.
// They are undefined only when Core sent none — never fabricated.

const REQUEST_ID = "req-abc-123";
const TRACEPARENT = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

function correlatedResponse(status, body, headers) {
  const text = body === undefined ? "" : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: (name) => headers[name.toLowerCase()] ?? null },
    text: () => Promise.resolve(text),
  };
}

test("the echoed X-Request-Id and traceparent surface on the success envelope (CORE-SDX-001)", async () => {
  const { client } = makeClient({
    handler: () =>
      correlatedResponse(
        200,
        {
          id: "ws-1",
          organizationId: "org-1",
          slug: "demo",
          name: "Demo",
          status: "Active",
          createdAt: "2026-06-13T00:00:00+00:00",
          updatedAt: "2026-06-13T00:00:00+00:00",
          version: "7",
        },
        {
          "x-request-id": REQUEST_ID,
          traceparent: TRACEPARENT,
          etag: 'W/"7"',
        },
      ),
  });

  const read = await client.workspaces.getWithETag("ws-1", {
    organizationSlug: "acme",
  });

  assert.equal(read.requestId, REQUEST_ID);
  assert.equal(read.traceparent, TRACEPARENT);
  // The existing envelope fields are unchanged.
  assert.equal(read.etag, 'W/"7"');
  assert.equal(read.data.version, "7");
});

test("the echoed X-Request-Id and traceparent surface on the thrown error (CORE-SDX-001)", async () => {
  const { client } = makeClient({
    handler: () =>
      correlatedResponse(
        403,
        { title: "Forbidden", status: 403 },
        { "x-request-id": REQUEST_ID, traceparent: TRACEPARENT },
      ),
  });

  let captured;
  try {
    await client.workspaces.list({ organizationSlug: "acme" });
  } catch (error) {
    captured = error;
  }

  assert.ok(isLiveCoreApiError(captured));
  assert.equal(captured.requestId, REQUEST_ID);
  assert.equal(captured.traceparent, TRACEPARENT);
  // The correlation ids are non-sensitive identifiers; the token is still never embedded.
  assert.ok(!String(captured.message).includes(TOKEN));
});

test("a response without correlation headers leaves requestId/traceparent undefined on both paths — never fabricated (CORE-SDX-001)", async () => {
  // The jsonResponse helper's headers.get always returns null (no headers set).
  const body = {
    id: "ws-1",
    organizationId: "org-1",
    slug: "demo",
    name: "Demo",
    status: "Active",
    createdAt: "2026-06-13T00:00:00+00:00",
    updatedAt: "2026-06-13T00:00:00+00:00",
    version: "7",
  };
  const { client } = makeClient({ handler: () => jsonResponse(200, body) });

  const read = await client.workspaces.getWithETag("ws-1", {
    organizationSlug: "acme",
  });
  assert.equal(read.requestId, undefined);
  assert.equal(read.traceparent, undefined);

  const { client: denying } = makeClient({
    handler: () => jsonResponse(403, { title: "Forbidden", status: 403 }),
  });
  let captured;
  try {
    await denying.workspaces.list({ organizationSlug: "acme" });
  } catch (error) {
    captured = error;
  }
  assert.ok(isLiveCoreApiError(captured));
  assert.equal(captured.requestId, undefined);
  assert.equal(captured.traceparent, undefined);
});

test("a blank correlation header is treated as absent, not surfaced as an empty id (CORE-SDX-001)", async () => {
  const { client } = makeClient({
    handler: () =>
      correlatedResponse(
        200,
        { id: "ws-1", version: "7" },
        { "x-request-id": "   ", traceparent: "" },
      ),
  });

  const read = await client.workspaces.getWithETag("ws-1", {
    organizationSlug: "acme",
  });
  assert.equal(read.requestId, undefined);
  assert.equal(read.traceparent, undefined);
});

test("generateRequestId is sent as the X-Request-Id header", async () => {
  const { client, calls } = makeClient({
    generateRequestId: () => "req-42",
    handler: () => jsonResponse(200, []),
  });

  await client.workspaces.list({ organizationSlug: "acme" });

  assert.equal(calls[0].init.headers["X-Request-Id"], "req-42");
});

test("an async token provider is awaited and its token attached", async () => {
  const { client, calls } = makeClient({
    getAccessToken: () => Promise.resolve("async-token"),
    handler: () => jsonResponse(200, []),
  });

  await client.workspaces.list({ organizationSlug: "acme" });

  assert.equal(calls[0].init.headers.Authorization, "Bearer async-token");
});

test("a transport failure surfaces as a LiveCoreError", async () => {
  const client = new LiveCoreClient({
    baseUrl: BASE_URL,
    getAccessToken: () => TOKEN,
    fetch: () => Promise.reject(new Error("socket closed")),
  });

  await assert.rejects(
    () => client.workspaces.list({ organizationSlug: "acme" }),
    (error) => {
      assert.ok(error instanceof LiveCoreError);
      assert.ok(!(error instanceof LiveCoreApiError));
      return true;
    },
  );
});

test("constructing without a baseUrl fails fast", () => {
  assert.throws(
    () =>
      new LiveCoreClient({
        baseUrl: "",
        getAccessToken: () => TOKEN,
        fetch: () => Promise.resolve(jsonResponse(200, {})),
      }),
    LiveCoreError,
  );
});

// --- CORE-SDK-006: the completed SDK covers every implemented v1 route. ----------
// Per-resource transport assertions for the new methods: the method/path/verb the
// request carries, the bearer token on every call, the body/query forwarding, and
// the `204 No Content` deletes resolving to `undefined`. Plus fail-closed negative
// checks that a server denial on a new, security-relevant route surfaces as a typed
// error, never a success value.

const OK_OBJECT = () => jsonResponse(200, {});
const NO_CONTENT = () => jsonResponse(204, undefined);

test("identity.getCurrentPrincipal reads GET /me with the bearer token and no query", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, { user: { id: "u-1" }, memberships: [] }),
  });

  const me = await client.identity.getCurrentPrincipal();

  assert.equal(me.user.id, "u-1");
  assert.equal(calls[0].url, "https://core.example.test/api/v1/me");
  assert.equal(calls[0].init.method, "GET");
  assert.equal(calls[0].init.headers.Authorization, "Bearer test-access-token");
});

test("organizations resource maps list/create/delete/erase/export to the right verbs and paths", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.organizations.list();
  assert.equal(calls[0].url, "https://core.example.test/api/v1/organizations");
  assert.equal(calls[0].init.method, "GET");

  await client.organizations.create({ slug: "acme", name: "Acme" });
  assert.equal(calls[1].url, "https://core.example.test/api/v1/organizations");
  assert.equal(calls[1].init.method, "POST");
  assert.deepEqual(JSON.parse(calls[1].init.body), {
    slug: "acme",
    name: "Acme",
  });

  await client.organizations.exportMemberPersonalData("acme", "m-1");
  assert.equal(
    calls[2].url,
    "https://core.example.test/api/v1/organizations/acme/members/m-1/personal-data-export",
  );
  assert.equal(calls[2].init.method, "GET");
});

test("organizations deletes use DELETE and a 204 resolves to undefined", async () => {
  const { client, calls } = makeClient({ handler: NO_CONTENT });

  assert.equal(await client.organizations.delete("acme"), undefined);
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/organizations/acme",
  );
  assert.equal(calls[0].init.method, "DELETE");
  assert.equal(calls[0].init.body, undefined);

  assert.equal(
    await client.organizations.eraseMemberPersonalData("acme", "m-1"),
    undefined,
  );
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/organizations/acme/members/m-1/personal-data",
  );
  assert.equal(calls[1].init.method, "DELETE");
});

test("audit.list reads GET /audit-logs with the org slug and paging", async () => {
  const page = {
    organizationId: "org-1",
    offset: 0,
    limit: 50,
    hasMore: false,
    entries: [],
  };
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, page),
  });

  const result = await client.audit.list({
    organizationSlug: "acme",
    limit: 25,
    offset: 50,
  });

  assert.deepEqual(result, page);
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/audit-logs?organizationSlug=acme&limit=25&offset=50",
  );
  assert.equal(calls[0].init.method, "GET");
});

test("templates resource maps list/get/create/delete to org-scoped paths", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.templates.list("acme");
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/organizations/acme/templates",
  );
  assert.equal(calls[0].init.method, "GET");

  await client.templates.get("acme", "t-1");
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/organizations/acme/templates/t-1",
  );

  await client.templates.create("acme", { templateKey: "k", definition: "{}" });
  assert.equal(calls[2].init.method, "POST");
  assert.deepEqual(JSON.parse(calls[2].init.body), {
    templateKey: "k",
    definition: "{}",
  });

  const { client: c2, calls: d } = makeClient({ handler: NO_CONTENT });
  assert.equal(await c2.templates.delete("acme", "t-1"), undefined);
  assert.equal(d[0].init.method, "DELETE");
  assert.equal(
    d[0].url,
    "https://core.example.test/api/v1/organizations/acme/templates/t-1",
  );
});

test("recaps.getSessionRecap reads GET /sessions/{id}/recap with the org slug", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, { id: "r-1", sessionId: "sess-1" }),
  });

  await client.recaps.getSessionRecap("sess-1", { organizationSlug: "acme" });

  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/sessions/sess-1/recap?organizationSlug=acme",
  );
  assert.equal(calls[0].init.method, "GET");
});

test("workspace archive POSTs with the org slug and honors an optional If-Match", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, { id: "ws-1", version: "9" }),
  });

  await client.workspaces.archive("ws-1", { organizationSlug: "acme" });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/archive?organizationSlug=acme",
  );
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.headers["If-Match"], undefined);

  await client.workspaces.archive(
    "ws-1",
    { organizationSlug: "acme" },
    { ifMatch: 'W/"9"' },
  );
  assert.equal(calls[1].init.headers["If-Match"], 'W/"9"');
});

test("workspace invitations: list pages, accept POSTs the token body, revoke/remove are 204 DELETEs", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.workspaces.listInvitations("ws-1", { organizationSlug: "acme" });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/invitations?organizationSlug=acme",
  );
  assert.equal(calls[0].init.method, "GET");

  await client.workspaces.acceptInvitation("ws-1", {
    organizationSlug: "acme",
    token: "plain-token",
  });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/workspaces/ws-1/invitations/accept",
  );
  assert.equal(calls[1].init.method, "POST");
  assert.deepEqual(JSON.parse(calls[1].init.body), {
    organizationSlug: "acme",
    token: "plain-token",
  });

  const { client: c2, calls: d } = makeClient({ handler: NO_CONTENT });
  assert.equal(
    await c2.workspaces.revokeInvitation("ws-1", "inv-1", {
      organizationSlug: "acme",
    }),
    undefined,
  );
  assert.equal(d[0].init.method, "DELETE");
  assert.equal(
    d[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/invitations/inv-1?organizationSlug=acme",
  );

  assert.equal(
    await c2.workspaces.removeMember("ws-1", "m-1", {
      organizationSlug: "acme",
    }),
    undefined,
  );
  assert.equal(
    d[1].url,
    "https://core.example.test/api/v1/workspaces/ws-1/members/m-1?organizationSlug=acme",
  );
});

test("sessions: list/create/get and participant join/leave map to the right routes", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.sessions.list("ws-1", { organizationSlug: "acme" });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/sessions?organizationSlug=acme",
  );

  await client.sessions.create("ws-1", {
    organizationSlug: "acme",
    title: "Demo",
  });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/workspaces/ws-1/sessions",
  );
  assert.equal(calls[1].init.method, "POST");
  assert.deepEqual(JSON.parse(calls[1].init.body), {
    organizationSlug: "acme",
    title: "Demo",
  });

  await client.sessions.get("sess-1", { organizationSlug: "acme" });
  assert.equal(
    calls[2].url,
    "https://core.example.test/api/v1/sessions/sess-1?organizationSlug=acme",
  );

  await client.sessions.joinParticipant("sess-1", "p-1", {
    organizationSlug: "acme",
  });
  assert.equal(
    calls[3].url,
    "https://core.example.test/api/v1/sessions/sess-1/participants/p-1/join?organizationSlug=acme",
  );
  assert.equal(calls[3].init.method, "POST");

  await client.sessions.leaveParticipant("sess-1", "p-1", {
    organizationSlug: "acme",
  });
  assert.equal(
    calls[4].url,
    "https://core.example.test/api/v1/sessions/sess-1/participants/p-1/leave?organizationSlug=acme",
  );
});

test("scenes: get/reorder/delete map to the right verbs and paths", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.scenes.get("sc-1", { organizationSlug: "acme" });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/scenes/sc-1?organizationSlug=acme",
  );

  await client.scenes.reorder("ws-1", "sc-1", {
    organizationSlug: "acme",
    targetIndex: 2,
  });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/workspaces/ws-1/scenes/sc-1/reorder",
  );
  assert.equal(calls[1].init.method, "POST");
  assert.deepEqual(JSON.parse(calls[1].init.body), {
    organizationSlug: "acme",
    targetIndex: 2,
  });

  const { client: c2, calls: d } = makeClient({ handler: NO_CONTENT });
  assert.equal(
    await c2.scenes.delete("ws-1", "sc-1", { organizationSlug: "acme" }),
    undefined,
  );
  assert.equal(d[0].init.method, "DELETE");
  assert.equal(
    d[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/scenes/sc-1?organizationSlug=acme",
  );
});

test("content: list/read/delete map to scene-scoped content-block routes", async () => {
  const { client, calls } = makeClient({ handler: OK_OBJECT });

  await client.content.listBlocks("sc-1", { organizationSlug: "acme" });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/scenes/sc-1/content-blocks?organizationSlug=acme",
  );

  await client.content.getBlock("sc-1", "cb-1", { organizationSlug: "acme" });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/scenes/sc-1/content-blocks/cb-1?organizationSlug=acme",
  );

  const { client: c2, calls: d } = makeClient({ handler: NO_CONTENT });
  assert.equal(
    await c2.content.deleteBlock("sc-1", "cb-1", { organizationSlug: "acme" }),
    undefined,
  );
  assert.equal(d[0].init.method, "DELETE");
  assert.equal(
    d[0].url,
    "https://core.example.test/api/v1/scenes/sc-1/content-blocks/cb-1?organizationSlug=acme",
  );
});

test("entities: entity delete and entity-relationship delete are 204 DELETEs", async () => {
  const { client, calls } = makeClient({ handler: NO_CONTENT });

  assert.equal(
    await client.entities.delete("ws-1", "e-1", { organizationSlug: "acme" }),
    undefined,
  );
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/workspaces/ws-1/entities/e-1?organizationSlug=acme",
  );
  assert.equal(calls[0].init.method, "DELETE");

  assert.equal(
    await client.entities.deleteRelationship("ws-1", "rel-1", {
      organizationSlug: "acme",
    }),
    undefined,
  );
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/workspaces/ws-1/entity-relationships/rel-1?organizationSlug=acme",
  );
});

test("visibility.hide POSTs the body and sends the Idempotency-Key header", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(200, {
        resourceType: "Scene",
        resourceId: "sc-1",
        visible: false,
        outcome: "Applied",
        participantId: null,
      }),
  });

  const result = await client.visibility.hide(
    "sess-1",
    { organizationSlug: "acme", resourceType: "Scene", resourceId: "sc-1" },
    { idempotencyKey: "hide-key-1" },
  );

  assert.equal(result.visible, false);
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/sessions/sess-1/hide",
  );
  assert.equal(calls[0].init.method, "POST");
  assert.equal(calls[0].init.headers["Idempotency-Key"], "hide-key-1");
  assert.deepEqual(JSON.parse(calls[0].init.body), {
    organizationSlug: "acme",
    resourceType: "Scene",
    resourceId: "sc-1",
  });
});

test("assets: link delete and asset delete are 204 DELETEs", async () => {
  const { client, calls } = makeClient({ handler: NO_CONTENT });

  assert.equal(
    await client.assets.deleteLink("a-1", "l-1", { organizationSlug: "acme" }),
    undefined,
  );
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/assets/a-1/links/l-1?organizationSlug=acme",
  );
  assert.equal(calls[0].init.method, "DELETE");

  assert.equal(
    await client.assets.delete("a-1", { organizationSlug: "acme" }),
    undefined,
  );
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/assets/a-1?organizationSlug=acme",
  );
});

test("getDownloadUrl forwards the sessionId query when provided and omits it otherwise (CORE-DX-010)", async () => {
  const { client, calls } = makeClient({
    handler: () =>
      jsonResponse(200, {
        assetId: "a-1",
        status: "Available",
        contentType: "application/pdf",
        downloadUrl: "https://storage.example.test/a-1?sig=x",
        expiresAt: "2026-06-26T00:15:00+00:00",
      }),
  });

  // An audience caller forwards the session an asset was revealed to them in, as the
  // ?sessionId= query parameter (the session-scoped audience download path).
  await client.assets.getDownloadUrl("a-1", {
    organizationSlug: "acme",
    sessionId: "sess-1",
  });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/assets/a-1/download-url?organizationSlug=acme&sessionId=sess-1",
  );
  assert.equal(calls[0].init.method, "GET");

  // Omitting sessionId preserves the prior host-path URL — no sessionId parameter.
  await client.assets.getDownloadUrl("a-1", { organizationSlug: "acme" });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/assets/a-1/download-url?organizationSlug=acme",
  );
});

test("an audience caller downloads a revealed asset by passing sessionId, is 400 without it, and is refused an asset not revealed to them (CORE-DX-010, server-gated)", async () => {
  // A fake Core that gates the audience download exactly as the server does
  // (CORE-SVIS-003/004): an audience caller MUST name the session, and may obtain a
  // URL only for an asset revealed to them IN THAT session. "revealed-asset" is
  // revealed to the caller in "sess-revealed"; "secret-asset" is not revealed to them.
  const { client } = makeClient({
    handler: (url) => {
      const query = new URL(url).searchParams;
      const assetId = url.split("/assets/")[1].split("/")[0];
      const sessionId = query.get("sessionId");
      if (sessionId === null) {
        // The audience download is session-scoped; omitting the session is the
        // request-shape 400 this story fixes (the SDK previously could not send one).
        return jsonResponse(400, {
          title: "Bad Request",
          status: 400,
          code: "validation_error",
        });
      }
      if (assetId === "revealed-asset" && sessionId === "sess-revealed") {
        return jsonResponse(200, {
          assetId,
          status: "Available",
          contentType: "application/pdf",
          downloadUrl: "https://storage.example.test/revealed?sig=x",
          expiresAt: "2026-06-26T00:15:00+00:00",
        });
      }
      // Linked but NOT revealed to this caller in this session: a server-gated denial.
      return jsonResponse(403, { title: "Forbidden", status: 403 });
    },
  });

  // POSITIVE: naming the revealed session yields the signed download URL — the fix.
  const ok = await client.assets.getDownloadUrl("revealed-asset", {
    organizationSlug: "acme",
    sessionId: "sess-revealed",
  });
  assert.equal(ok.status, "Available");
  assert.ok(ok.downloadUrl.startsWith("https://"));

  // REGRESSION the story fixes: an audience download with no sessionId stays a 400.
  await assert.rejects(
    () =>
      client.assets.getDownloadUrl("revealed-asset", {
        organizationSlug: "acme",
      }),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 400);
      return true;
    },
  );

  // NEGATIVE (server-gated): an asset NOT revealed to this caller is refused even
  // with a valid session — the SDK never turns the denial into a success value.
  await assert.rejects(
    () =>
      client.assets.getDownloadUrl("secret-asset", {
        organizationSlug: "acme",
        sessionId: "sess-revealed",
      }),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 403);
      return true;
    },
  );
});

test("each dedupe-capable create forwards a supplied Idempotency-Key header (CORE-DX-008)", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(201, { id: "r-1" }),
  });

  await client.workspaces.create(
    { organizationSlug: "acme", slug: "demo", name: "Demo" },
    { idempotencyKey: "ws-key" },
  );
  await client.sessions.create(
    "ws-1",
    { organizationSlug: "acme", title: "Demo" },
    { idempotencyKey: "sess-key" },
  );
  await client.scenes.create(
    "ws-1",
    { organizationSlug: "acme", title: "Scene" },
    { idempotencyKey: "scene-key" },
  );
  await client.content.createBlock(
    "sc-1",
    { organizationSlug: "acme" },
    { type: "Text", body: { text: "hi" } },
    { idempotencyKey: "cb-key" },
  );
  await client.assets.createLink(
    "a-1",
    { organizationSlug: "acme", targetType: "ContentBlock", targetId: "cb-1" },
    { idempotencyKey: "link-key" },
  );
  await client.entities.create(
    "ws-1",
    { organizationSlug: "acme", entityTypeId: "et-1", name: "Lantern" },
    { idempotencyKey: "entity-key" },
  );
  await client.exports.createExport(
    "ws-1",
    { organizationSlug: "acme" },
    { idempotencyKey: "export-key" },
  );

  // The supplied key rides as the Idempotency-Key header on each covered create,
  // through the same RequestSpec.idempotencyKey transport seam reveal/hide use.
  assert.deepEqual(
    calls.map((c) => c.init.headers["Idempotency-Key"]),
    [
      "ws-key",
      "sess-key",
      "scene-key",
      "cb-key",
      "link-key",
      "entity-key",
      "export-key",
    ],
  );
  assert.ok(calls.every((c) => c.init.method === "POST"));
});

test("an omitted idempotency option sends no Idempotency-Key header on a create (non-breaking) (CORE-DX-008)", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(201, { id: "r-1" }),
  });

  // No options argument: the call is unchanged from the prior, header-less create.
  await client.workspaces.create({
    organizationSlug: "acme",
    slug: "demo",
    name: "Demo",
  });
  await client.sessions.create("ws-1", {
    organizationSlug: "acme",
    title: "Demo",
  });
  await client.scenes.create("ws-1", {
    organizationSlug: "acme",
    title: "Scene",
  });
  await client.content.createBlock(
    "sc-1",
    { organizationSlug: "acme" },
    { type: "Text", body: { text: "hi" } },
  );
  await client.assets.createLink("a-1", {
    organizationSlug: "acme",
    targetType: "ContentBlock",
    targetId: "cb-1",
  });
  await client.entities.create("ws-1", {
    organizationSlug: "acme",
    entityTypeId: "et-1",
    name: "Lantern",
  });
  await client.exports.createExport("ws-1", { organizationSlug: "acme" });

  assert.ok(
    calls.every((c) => c.init.headers["Idempotency-Key"] === undefined),
  );
});

test("a reused Idempotency-Key replays the original created resource through the SDK — count stays 1 (CORE-DX-008/CORE-DX-004)", async () => {
  // A fake Core that dedupes create POSTs exactly as the server does (CORE-DX-004):
  // the FIRST request under a key records and returns the new resource (201); a
  // RETRY under the SAME key returns the ORIGINAL resource (200) and creates
  // nothing. A different key creates a new resource.
  const store = new Map();
  let created = 0;
  const { client, calls } = makeClient({
    handler: (_url, init) => {
      const key = init.headers["Idempotency-Key"];
      if (key !== undefined && store.has(key)) {
        return jsonResponse(200, store.get(key));
      }
      created += 1;
      const resource = {
        id: `ws-${created}`,
        organizationId: "org-1",
        slug: "demo",
        name: "Demo",
        createdAt: "2026-06-22T00:00:00+00:00",
        updatedAt: "2026-06-22T00:00:00+00:00",
      };
      if (key !== undefined) {
        store.set(key, resource);
      }
      return jsonResponse(201, resource);
    },
  });

  const request = { organizationSlug: "acme", slug: "demo", name: "Demo" };
  const first = await client.workspaces.create(request, {
    idempotencyKey: "retry-key",
  });
  const replay = await client.workspaces.create(request, {
    idempotencyKey: "retry-key",
  });

  // The retry replays the original resource, and the server created exactly one.
  assert.equal(first.id, "ws-1");
  assert.equal(replay.id, first.id);
  assert.equal(created, 1);
  assert.equal(calls.length, 2);
  assert.ok(
    calls.every((c) => c.init.headers["Idempotency-Key"] === "retry-key"),
  );

  // A DIFFERENT key is a different logical create — it produces a new resource.
  const other = await client.workspaces.create(request, {
    idempotencyKey: "other-key",
  });
  assert.equal(other.id, "ws-2");
  assert.equal(created, 2);
});

test("entitlements.getMyEntitlements reads GET /me/entitlements", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, { entitlements: [] }),
  });

  await client.entitlements.getMyEntitlements();

  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/me/entitlements",
  );
  assert.equal(calls[0].init.method, "GET");
});

test("a non-privileged audit read surfaces a 403 as a LiveCoreApiError, never a page", async () => {
  // Fail-closed: a role that may not read the audit log is denied server-side; the
  // SDK never turns that denial into an empty/success page.
  const { client } = makeClient({
    handler: () =>
      jsonResponse(403, {
        title: "Forbidden",
        status: 403,
        code: "permission_denied",
      }),
  });

  await assert.rejects(
    () => client.audit.list({ organizationSlug: "acme" }),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 403);
      return true;
    },
  );
});

test("a foreign-tenant organization delete surfaces a 404 as a LiveCoreApiError", async () => {
  // Fail-closed: a tenant the caller cannot see is hidden as 404; the destructive
  // delete must surface that as a typed error, never a silent success.
  const { client } = makeClient({
    handler: () =>
      jsonResponse(404, { title: "Not Found", status: 404, code: "not_found" }),
  });

  await assert.rejects(
    () => client.organizations.delete("foreign-org"),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 404);
      return true;
    },
  );
});

test("a personal-data export to an unentitled member surfaces a 403 as a LiveCoreApiError", async () => {
  const { client } = makeClient({
    handler: () =>
      jsonResponse(403, {
        title: "Forbidden",
        status: 403,
        code: "permission_denied",
      }),
  });

  await assert.rejects(
    () => client.organizations.exportMemberPersonalData("acme", "m-1"),
    (error) => {
      assert.ok(isLiveCoreApiError(error));
      assert.equal(error.status, 403);
      return true;
    },
  );
});

test("fail closed: a destructive delete sends no request when no token is available", async () => {
  const { client, calls } = makeClient({ getAccessToken: () => "" });

  await assert.rejects(
    () => client.organizations.delete("acme"),
    (error) => {
      assert.ok(error instanceof LiveCoreError);
      assert.ok(!(error instanceof LiveCoreApiError));
      return true;
    },
  );
  assert.equal(calls.length, 0);
});

// --- Package versioning and changelog process (CORE-SDK-005). ------------------
// The runtime VERSION, the package manifest and the CHANGELOG must all agree, so
// a release cannot ship with the version constant, package.json or the changelog
// out of step. The packages are versioned together (lockstep); see
// docs/23_PACKAGE_VERSIONING.md.

const PACKAGE_DIR = new URL("../", import.meta.url);
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const manifest = JSON.parse(
  readFileSync(new URL("package.json", PACKAGE_DIR), "utf8"),
);
const changelog = readFileSync(new URL("CHANGELOG.md", PACKAGE_DIR), "utf8");

test("the built package exposes a well-formed SemVer version", () => {
  assert.equal(typeof VERSION, "string");
  assert.match(VERSION, SEMVER);
});

test("the exported VERSION matches the package manifest version", () => {
  assert.equal(VERSION, manifest.version);
});

test("the CHANGELOG documents the current version and ships with the package", () => {
  const headingLine = `## [${VERSION}]`;
  assert.ok(
    changelog.split(/\r?\n/).some((line) => line.startsWith(headingLine)),
    `CHANGELOG must document version ${VERSION} under a "## [${VERSION}]" heading`,
  );
  assert.ok(
    manifest.files.includes("CHANGELOG.md"),
    "package.json files must include CHANGELOG.md so it ships to consumers",
  );
});

// --- The AGPL LICENSE and third-party NOTICE ship with the package (CORE-LIC-003). ---
// The Core is AGPL-3.0-or-later and redistributes attribution-requiring
// dependencies, so every package tarball must carry the AGPL LICENSE (with
// CORE-PUB-001) and the generated third-party NOTICE inventory. Both are listed in
// files[] and the shipped LICENSE is byte-identical to the repository-root AGPL text.
test("the AGPL LICENSE and third-party NOTICE ship with the package (CORE-LIC-003)", () => {
  for (const shipped of ["LICENSE", "THIRD-PARTY-NOTICES.md"]) {
    assert.ok(
      manifest.files.includes(shipped),
      `package.json files must include ${shipped} so it ships in the tarball (CORE-LIC-003)`,
    );
  }
  const license = readFileSync(new URL("LICENSE", PACKAGE_DIR), "utf8").replace(
    /\r\n/g,
    "\n",
  );
  const rootLicense = readFileSync(
    new URL("../../LICENSE", PACKAGE_DIR),
    "utf8",
  ).replace(/\r\n/g, "\n");
  assert.ok(
    license.includes("GNU AFFERO GENERAL PUBLIC LICENSE"),
    "the shipped LICENSE must be the AGPL license text",
  );
  assert.equal(
    license,
    rootLicense,
    "the package LICENSE must be byte-identical to the repository-root AGPL LICENSE",
  );
  const notice = readFileSync(
    new URL("THIRD-PARTY-NOTICES.md", PACKAGE_DIR),
    "utf8",
  );
  assert.match(notice, /# Third-Party Notices/);
});
