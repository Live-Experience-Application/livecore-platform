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
import { test } from "node:test";

import {
  LiveCoreApiError,
  LiveCoreClient,
  LiveCoreError,
  PACKAGE_NAME,
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

test("list workspaces sends organizationSlug as a query parameter and no body", async () => {
  const { client, calls } = makeClient({
    handler: () => jsonResponse(200, []),
  });

  const result = await client.workspaces.list({ organizationSlug: "acme" });

  assert.deepEqual(result, []);
  const { url, init } = calls[0];
  assert.equal(
    url,
    "https://core.example.test/api/v1/workspaces?organizationSlug=acme",
  );
  assert.equal(init.method, "GET");
  assert.equal(init.body, undefined);
  assert.equal(init.headers["Content-Type"], undefined);
  assert.equal(init.headers.Authorization, "Bearer test-access-token");
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
    afterEventId: "e-9",
  });
  assert.equal(
    calls[0].url,
    "https://core.example.test/api/v1/sessions/sess-1/events?organizationSlug=acme&participantId=p-1&afterEventId=e-9",
  );

  await client.realtime.getSessionEvents("sess-1", {
    organizationSlug: "acme",
  });
  assert.equal(
    calls[1].url,
    "https://core.example.test/api/v1/sessions/sess-1/events?organizationSlug=acme",
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
