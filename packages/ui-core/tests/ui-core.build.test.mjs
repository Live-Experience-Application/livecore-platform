/**
 * Package build tests for @livecore/ui-core (CORE-SDK-004).
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
  DEFAULT_EMPHASIS,
  DEFAULT_SIZE,
  DEFAULT_SURFACE_LEVEL,
  DEFAULT_TONE,
  DEFAULT_VARIANT,
  Emphases,
  HeadingLevels,
  PACKAGE_NAME,
  PrimitiveKinds,
  Sizes,
  SurfaceLevels,
  TextWeights,
  Tones,
  VERSION,
  resolveVariant,
} from "../dist/index.js";

test("the built package exposes its stable name", () => {
  assert.equal(PACKAGE_NAME, "@livecore/ui-core");
});

test("the variant vocabularies are the stable generic option sets", () => {
  assert.deepEqual(
    [...Tones],
    [
      "neutral",
      "primary",
      "secondary",
      "accent",
      "success",
      "warning",
      "danger",
      "info",
    ],
  );
  assert.deepEqual([...Sizes], ["sm", "md", "lg"]);
  assert.deepEqual([...Emphases], ["solid", "soft", "outline", "ghost"]);
  assert.deepEqual([...SurfaceLevels], ["base", "raised", "overlay"]);
  assert.deepEqual([...TextWeights], ["regular", "medium", "semibold", "bold"]);
  assert.deepEqual([...HeadingLevels], [1, 2, 3, 4, 5, 6]);
});

test("the primitive catalog is the stable generic building-block set", () => {
  assert.deepEqual(
    [...PrimitiveKinds],
    [
      "surface",
      "stack",
      "text",
      "heading",
      "button",
      "badge",
      "field",
      "spinner",
      "divider",
      "avatar",
    ],
  );
});

test("each default constant is a member of its own vocabulary", () => {
  assert.ok(Tones.includes(DEFAULT_TONE));
  assert.ok(Sizes.includes(DEFAULT_SIZE));
  assert.ok(Emphases.includes(DEFAULT_EMPHASIS));
  assert.ok(SurfaceLevels.includes(DEFAULT_SURFACE_LEVEL));
});

test("DEFAULT_VARIANT is composed of the documented per-field defaults", () => {
  assert.deepEqual(DEFAULT_VARIANT, {
    tone: DEFAULT_TONE,
    size: DEFAULT_SIZE,
    emphasis: DEFAULT_EMPHASIS,
  });
});

test("resolveVariant() applies every default when nothing is selected", () => {
  assert.deepEqual(resolveVariant(), {
    tone: DEFAULT_TONE,
    size: DEFAULT_SIZE,
    emphasis: DEFAULT_EMPHASIS,
  });
  // Default argument: calling with no object behaves the same as `{}`.
  assert.deepEqual(resolveVariant(), resolveVariant({}));
});

test("resolveVariant() keeps selected fields and defaults the rest", () => {
  assert.deepEqual(resolveVariant({ tone: "primary" }), {
    tone: "primary",
    size: DEFAULT_SIZE,
    emphasis: DEFAULT_EMPHASIS,
  });
  assert.deepEqual(
    resolveVariant({ tone: "danger", size: "lg", emphasis: "outline" }),
    { tone: "danger", size: "lg", emphasis: "outline" },
  );
});

test("resolveVariant() is pure and does not mutate its argument", () => {
  const selection = { tone: "success" };
  const resolved = resolveVariant(selection);
  assert.deepEqual(selection, { tone: "success" });
  assert.notEqual(resolved, selection);
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
