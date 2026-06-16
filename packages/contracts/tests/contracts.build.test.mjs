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
  API_BASE_PATH,
  AssetStatuses,
  ContentBlockTypes,
  CoreErrorStatusCodes,
  KnownSessionEventTypes,
  MembershipRoles,
  PACKAGE_NAME,
  ProblemCodes,
  PurchaseProviders,
  RequestHeaders,
  SessionStatuses,
  VERSION,
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

test("the transport constants are exported", () => {
  assert.equal(RequestHeaders.IdempotencyKey, "Idempotency-Key");
  assert.ok(CoreErrorStatusCodes.includes(404));
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
  const heading = new RegExp(`^## \\[${VERSION.replace(/\./g, "\\.")}\\]`, "m");
  assert.match(changelog, heading);
  assert.ok(
    manifest.files.includes("CHANGELOG.md"),
    "package.json files must include CHANGELOG.md so it ships to consumers",
  );
});
