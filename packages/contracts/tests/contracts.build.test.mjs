/**
 * Package build tests for @livecore/contracts (CORE-SDK-001).
 *
 * These run with the Node built-in test runner (no new dependency) against the
 * COMPILED package output in `dist/`, so they fail if the package does not build
 * or does not expose its stable runtime surface. The package `test` script builds
 * `dist/` first, then runs this file with `node --test`.
 */
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  API_BASE_PATH,
  AssetStatuses,
  ContentBlockTypes,
  CoreErrorStatusCodes,
  KnownSessionEventTypes,
  MembershipRoles,
  PACKAGE_NAME,
  PurchaseProviders,
  RequestHeaders,
  SessionStatuses,
  VisibilityResourceTypes,
} from "../dist/index.js";

test("the built package exposes its stable name", () => {
  assert.equal(PACKAGE_NAME, "@livecore/contracts");
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
  assert.deepEqual([...SessionStatuses], ["Prepared", "Live", "Ended"]);
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

test("the transport constants are exported", () => {
  assert.equal(RequestHeaders.IdempotencyKey, "Idempotency-Key");
  assert.ok(CoreErrorStatusCodes.includes(404));
});
