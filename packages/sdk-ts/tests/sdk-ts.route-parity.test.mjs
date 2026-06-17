/**
 * SDK ↔ api_routes coverage parity gate tests for @livecore/sdk-ts (CORE-SDK-007).
 *
 * The TypeScript-side mirror of spec-consistency check 6 (which binds
 * csv/api_routes.csv to the implemented minimal-API routes): the typed SDK must
 * expose exactly one client method per documented non-callback `/api/v1` route, so
 * the SDK can never silently fall behind the API again. This file proves the gate
 * REJECTS seeded drift — a route with no SDK method, an SDK method with no route,
 * and a method that calls a forbidden provider-callback route — over fixtures, then
 * proves the REAL repository tree passes. The same seeded-drift + real-tree pattern
 * as the session-event drift gate (CORE-RT-008) and the PowerShell spec-consistency
 * tests.
 *
 * Run with the Node built-in test runner (no new dependency). The gate reads SOURCE
 * (csv/api_routes.csv and the resource client sources), so it needs no build.
 */
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  diffRouteParity,
  findRouteParityDrift,
  parseCsvRecord,
  parseDocumentedRoutes,
  parseSdkRoutes,
  routeKey,
} from "../scripts/route-parity-drift.mjs";

// A small, internally-consistent fixture: one documented consumer route covered by
// one SDK method, plus one provider-callback route the SDK correctly does NOT call.
// Mutating a clone of it seeds each kind of drift below.
function consistentFixture() {
  return {
    documented: {
      consumer: new Set([routeKey("GET", "/api/v1/me")]),
      callback: new Set([
        routeKey("POST", "/api/v1/store-notifications/apple"),
      ]),
    },
    sdkRoutes: [
      {
        file: "src/resources/identity.ts",
        method: "GET",
        path: "/api/v1/me",
        key: routeKey("GET", "/api/v1/me"),
      },
    ],
  };
}

function findingsMatching(findings, pattern) {
  return findings.filter((finding) => pattern.test(finding));
}

test("a SDK that covers exactly the consumer routes (callbacks excepted) has no findings", () => {
  assert.deepEqual(diffRouteParity(consistentFixture()), []);
});

test("the provider callback route is not flagged when the SDK omits it (it is excepted)", () => {
  // The Apple store-notification receiver is a provider callback; the SDK correctly
  // exposes no method for it, and the gate must not demand one.
  const findings = diffRouteParity(consistentFixture());
  assert.equal(findingsMatching(findings, /store-notifications/).length, 0);
});

test("a seeded route with NO SDK method fails the gate", () => {
  const fixture = consistentFixture();
  fixture.documented.consumer.add(routeKey("POST", "/api/v1/widgets"));
  const findings = diffRouteParity(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /ROUTE: csv\/api_routes\.csv route 'POST \/api\/v1\/widgets' has no @livecore\/sdk-ts client method/,
    ).length > 0,
    "a documented route the SDK does not cover must fail",
  );
});

test("a seeded SDK method with NO route fails the gate", () => {
  const fixture = consistentFixture();
  fixture.sdkRoutes.push({
    file: "src/resources/phantom.ts",
    method: "GET",
    path: "/api/v1/phantom",
    key: routeKey("GET", "/api/v1/phantom"),
  });
  const findings = diffRouteParity(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /SDK: @livecore\/sdk-ts \(src\/resources\/phantom\.ts\) calls 'GET \/api\/v1\/phantom' which is not a route/,
    ).length > 0,
    "an SDK method calling a route absent from csv/api_routes.csv must fail",
  );
});

test("a seeded SDK method for a provider-callback route fails the gate", () => {
  const fixture = consistentFixture();
  fixture.sdkRoutes.push({
    file: "src/resources/store.ts",
    method: "POST",
    path: "/api/v1/store-notifications/apple",
    key: routeKey("POST", "/api/v1/store-notifications/apple"),
  });
  const findings = diffRouteParity(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /CALLBACK: .*exposes a method for 'POST \/api\/v1\/store-notifications\/apple', a provider-callback route the SDK must not call/,
    ).length > 0,
    "an SDK method for a provider callback must fail (callbacks are excepted, not exposed)",
  );
});

test("a seeded RENAMED route fails the gate in both directions", () => {
  const fixture = consistentFixture();
  // The CSV now documents a renamed path; the SDK still calls the old one.
  fixture.documented.consumer = new Set([routeKey("GET", "/api/v1/principal")]);
  const findings = diffRouteParity(fixture);
  assert.ok(
    findingsMatching(findings, /route 'GET \/api\/v1\/principal' has no/)
      .length > 0,
    "the renamed documented route now has no SDK method",
  );
  assert.ok(
    findingsMatching(findings, /calls 'GET \/api\/v1\/me' which is not a route/)
      .length > 0,
    "the SDK method now calls a route the CSV no longer documents",
  );
});

// --- The parsers read the real source shapes correctly. ------------------------

test("routeKey collapses every path parameter to a placeholder", () => {
  assert.equal(
    routeKey("delete", "/api/v1/workspaces/{workspaceId}/members/{memberId}"),
    "DELETE /api/v1/workspaces/{}/members/{}",
  );
});

test("the CSV record parser keeps a quoted comma-bearing roles cell as one field", () => {
  const fields = parseCsvRecord(
    'DELETE,/api/v1/workspaces/{workspaceId}/members/{memberId},Workspaces,"Owner,Admin",Remove member; hidden-404',
  );
  assert.equal(fields[0], "DELETE");
  assert.equal(
    fields[1],
    "/api/v1/workspaces/{workspaceId}/members/{memberId}",
  );
  assert.equal(fields[3], "Owner,Admin");
  assert.equal(fields[4], "Remove member; hidden-404");
});

test("the documented-route parser partitions consumer vs provider-callback routes", () => {
  const { consumer, callback } = parseDocumentedRoutes(
    "method,route,module,roles,notes\n" +
      "GET,/api/v1/me,IdentityAccess,authenticated,Principal context\n" +
      'POST,/api/v1/workspaces,Workspaces,"Owner,Admin",Create workspace\n' +
      "POST,/api/v1/store-notifications/apple,Store,none (provider callback),Receive notifications\n",
  );
  assert.deepEqual(
    [...consumer].sort(),
    ["GET /api/v1/me", "POST /api/v1/workspaces"].sort(),
  );
  assert.deepEqual([...callback], ["POST /api/v1/store-notifications/apple"]);
});

test("the SDK parser reads method+path from a request spec and restores the /api/v1 prefix", () => {
  const source = [
    {
      file: "src/resources/sessions.ts",
      text:
        "return this.http.send({\n" +
        '  method: "POST",\n' +
        "  path: `/sessions/${encodeURIComponent(sessionId)}/participants/${encodeURIComponent(participantId)}/join`,\n" +
        "});\n",
    },
  ];
  const routes = parseSdkRoutes(source);
  assert.equal(routes.length, 1);
  assert.equal(routes[0].key, "POST /api/v1/sessions/{}/participants/{}/join");
});

test("the SDK parser reads a plain-string path and the ETag read variant", () => {
  const source = [
    {
      file: "src/resources/store.ts",
      text:
        'return this.http.send({ method: "POST", path: "/purchases/apple/transactions", body });\n' +
        "return this.http.sendWithETag({\n" +
        '  method: "GET",\n' +
        "  path: `/workspaces/${encodeURIComponent(id)}`,\n" +
        "});\n",
    },
  ];
  const keys = parseSdkRoutes(source).map((route) => route.key);
  assert.deepEqual(keys, [
    "POST /api/v1/purchases/apple/transactions",
    "GET /api/v1/workspaces/{}",
  ]);
});

test("the SDK parser excepts the live realtime client (a method with no http.send route)", () => {
  // `connect` opens the SignalR hub instead of issuing an /api/v1 request, so it has
  // no request-spec literal and must contribute no route key (CORE-SDK-007 / docs/23).
  const source = [
    {
      file: "src/resources/realtime.ts",
      text:
        "async connect(params, onEvent) {\n" +
        "  const connection = this.hubConnectionFactory({ url: this.buildHubUrl(params) });\n" +
        '  connection.on("SessionEvent", (e) => onEvent(e));\n' +
        "  await connection.start();\n" +
        "}\n" +
        "getSessionEvents(sessionId, params) {\n" +
        "  return this.http.send({\n" +
        '    method: "GET",\n' +
        "    path: `/sessions/${encodeURIComponent(sessionId)}/events`,\n" +
        "  });\n" +
        "}\n",
    },
  ];
  const keys = parseSdkRoutes(source).map((route) => route.key);
  assert.deepEqual(keys, ["GET /api/v1/sessions/{}/events"]);
});

// --- The real repository tree passes the gate (CORE-SDK-007). ------------------

test("the real @livecore/sdk-ts surface covers exactly the non-callback api_routes", () => {
  const { findings } = findRouteParityDrift();
  assert.deepEqual(
    findings,
    [],
    "the SDK has drifted from csv/api_routes.csv: a documented non-callback route " +
      "has no client method, or a client method calls a route the CSV does not " +
      "document. Reconcile packages/sdk-ts/src/resources and csv/api_routes.csv " +
      "(CORE-SDK-007).",
  );
});

test("the real tree exposes one SDK route per consumer route, and excepts the two provider callbacks", () => {
  // A guardrail on the fixtures above: the real surface is substantial and the SDK
  // route SET equals the documented consumer SET, so a no-op gate (e.g. an empty
  // parse) cannot pass unnoticed. The two store-notification callbacks are documented
  // and deliberately absent from the SDK.
  const { documented, sdkRoutes } = findRouteParityDrift();
  const sdkKeys = new Set(sdkRoutes.map((route) => route.key));

  assert.ok(
    documented.consumer.size >= 60,
    "expected the documented consumer surface to be substantial",
  );
  assert.equal(
    sdkKeys.size,
    documented.consumer.size,
    "the SDK route set must equal the documented consumer route set",
  );
  assert.equal(
    documented.callback.size,
    2,
    "exactly the two store-notification routes are provider callbacks",
  );
  for (const callbackKey of documented.callback) {
    assert.ok(
      !sdkKeys.has(callbackKey),
      `the SDK must not expose a method for the provider callback ${callbackKey}`,
    );
  }
});
