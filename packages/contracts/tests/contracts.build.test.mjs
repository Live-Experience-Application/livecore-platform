/**
 * Package build tests for @livecore/contracts (CORE-SDK-001).
 *
 * These run with the Node built-in test runner (no new dependency) against the
 * COMPILED package output in `dist/`, so they fail if the package does not build
 * or does not expose its stable runtime surface. The package `test` script builds
 * `dist/` first, then runs this file with `node --test`.
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

import {
  generateOpenApiTypes,
  GENERATED_TYPES_URL,
} from "../scripts/generate-openapi.mjs";

import {
  API_BASE_PATH,
  AssetStatuses,
  ContentBlockTypes,
  CoreErrorStatusCodes,
  KnownSessionEventTypes,
  MembershipRoles,
  PACKAGE_NAME,
  ProblemCodes,
  PurchaseProviders,
  REALTIME_ACCESS_TOKEN_QUERY_PARAM,
  RealtimeHubPaths,
  RequestHeaders,
  ResponseHeaders,
  SESSION_EVENT_CLIENT_METHOD,
  SessionStatuses,
  VERSION,
  VisibilityResourceTypes,
} from "../dist/index.js";

test("the built package exposes its stable name", () => {
  assert.equal(PACKAGE_NAME, "@livecore/contracts");
});

// --- The OpenAPI-derived types do not drift from the document (CORE-OAS-002). ----
// The drift gate, as a portable test: regenerate the contract types from the
// committed OpenAPI document in memory and assert they equal the committed
// src/openapi.ts byte-for-byte (line-ending insensitive — the repository stores LF
// but a Windows checkout may be CRLF). This is the same check as the package
// `check:openapi` script and the CI `typescript`-job drift step; running it here
// means `pnpm --recursive run test` fails if the server's OpenAPI document and the
// published contracts have diverged.
test("the committed OpenAPI-derived types match the document (CORE-OAS-002)", async () => {
  const committed = readFileSync(GENERATED_TYPES_URL, "utf8").replace(
    /\r\n/g,
    "\n",
  );
  const regenerated = await generateOpenApiTypes();
  assert.equal(
    committed,
    regenerated,
    "packages/contracts/src/openapi.ts is out of date with openapi/livecore-v1.json; " +
      "run `pnpm --filter @livecore/contracts run generate` and commit the result (CORE-OAS-002).",
  );
});

test("the built package exposes the versioned API base path", () => {
  assert.equal(API_BASE_PATH, "/api/v1");
});

test("the membership-role vocabulary matches the Core authorization matrix", () => {
  assert.deepEqual(
    [...MembershipRoles],
    ["Owner", "Admin", "Host", "CoHost", "Participant", "Observer", "Auditor"],
  );
});

test("the lifecycle and kind vocabularies are the stable Core names", () => {
  assert.deepEqual(
    [...SessionStatuses],
    ["Prepared", "Live", "Ended", "Cancelled"],
  );
  assert.deepEqual([...ContentBlockTypes], ["Text", "Media", "Data"]);
  assert.deepEqual(
    [...VisibilityResourceTypes],
    ["Scene", "ContentBlock", "Entity"],
  );
  assert.deepEqual([...AssetStatuses], ["Pending", "Available"]);
  assert.deepEqual([...PurchaseProviders], ["Apple", "Google"]);
});

test("the known session event catalog is exported for typed handling", () => {
  assert.ok(KnownSessionEventTypes.includes("ContentRevealed"));
});

test("the live realtime hub constants mirror the server (CORE-RT-007)", () => {
  // The contract mirror of the server's live SignalR path: the hub path
  // (RealtimeHubRoutes.SessionHub = /hubs + /session), the single client method
  // (SessionEventEnvelope.ClientMethod) and the query-string token parameter
  // (HubBearerToken.QueryParameter). A drift in any of these would silently break a
  // vertical's live connection, so they are pinned here as published constants.
  assert.equal(RealtimeHubPaths.session, "/hubs/session");
  assert.equal(SESSION_EVENT_CLIENT_METHOD, "SessionEvent");
  assert.equal(REALTIME_ACCESS_TOKEN_QUERY_PARAM, "access_token");
  // The hub is NOT a versioned REST route — it is mounted at the origin under /hubs,
  // outside the /api/v1 surface — so its path never carries the API base path.
  assert.ok(!RealtimeHubPaths.session.startsWith(API_BASE_PATH));
});

test("the transport constants are exported", () => {
  assert.equal(RequestHeaders.IdempotencyKey, "Idempotency-Key");
  assert.ok(CoreErrorStatusCodes.includes(404));
});

test("the conditional-write transport constants are exported (CORE-DX-002)", () => {
  // A consumer reads the resource's weak ETag from the response and echoes it back as
  // If-Match to make a later write conditional; the 412 status is part of the error set.
  assert.equal(RequestHeaders.IfMatch, "If-Match");
  assert.equal(ResponseHeaders.ETag, "ETag");
  assert.ok(CoreErrorStatusCodes.includes(412));
});

test("the browser-consumer response headers are exported (CORE-DX-005)", () => {
  // The headers the CORS policy exposes so a cross-origin browser SDK can read the rate-limit,
  // correlation and created-resource signals; the SDK surfaces Retry-After/RateLimit-* on its error type.
  assert.equal(ResponseHeaders.Location, "Location");
  assert.equal(ResponseHeaders.RetryAfter, "Retry-After");
  assert.equal(ResponseHeaders.RequestId, "X-Request-Id");
  assert.equal(ResponseHeaders.TraceParent, "traceparent");
  assert.equal(ResponseHeaders.RateLimitLimit, "RateLimit-Limit");
  assert.equal(ResponseHeaders.RateLimitRemaining, "RateLimit-Remaining");
  assert.equal(ResponseHeaders.RateLimitReset, "RateLimit-Reset");
});

test("the deprecation/sunset response headers are exported (CORE-DX-006)", () => {
  // A deprecated route signals its retirement with these CORS-exposed headers (RFC 8594 Sunset plus the
  // Deprecation header); a consumer reads them to get advance notice before a contract changes.
  assert.equal(ResponseHeaders.Deprecation, "Deprecation");
  assert.equal(ResponseHeaders.Sunset, "Sunset");
});

test("the Problem Details error-code catalog is the stable published set", () => {
  // The published catalog must mirror the server-side ProblemCodes catalog exactly
  // (apps/api/ProblemCodes.cs); the cross-language drift check is the .NET contract
  // test. This pins the published surface so it cannot change unnoticed (CORE-DX-001).
  assert.deepEqual(
    [...ProblemCodes],
    [
      "validation_error",
      "authentication_required",
      "permission_denied",
      "not_found",
      "conflict",
      "duplicate_resource",
      "quota_exceeded",
      "workspace_archived",
      "concurrency_conflict",
      "precondition_failed",
      "unprocessable_entity",
      "payload_too_large",
      "rate_limited",
      "internal_error",
      "service_unavailable",
    ],
  );

  // The three structurally-distinct 409s must be distinguishable by code.
  for (const code of [
    "quota_exceeded",
    "workspace_archived",
    "concurrency_conflict",
  ]) {
    assert.ok(ProblemCodes.includes(code), `${code} must be published`);
  }
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
