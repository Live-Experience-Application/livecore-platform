/**
 * Package build tests for @livecore/design-tokens (CORE-SDK-003).
 *
 * These run with the Node built-in test runner (no new dependency) against the
 * COMPILED package output in `dist/`, so they fail if the package does not build
 * or does not expose its stable runtime surface. The package `test` script builds
 * `dist/` first, then runs this file with `node --test`.
 */
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  BreakpointSteps,
  ColorRoles,
  ColorSchemes,
  FontWeightRoles,
  MotionDurationSteps,
  PACKAGE_NAME,
  RadiusSteps,
  ShadowSteps,
  SpacingSteps,
  baseTheme,
  defineTheme,
} from "../dist/index.js";

test("the built package exposes its stable name", () => {
  assert.equal(PACKAGE_NAME, "@livecore/design-tokens");
});

test("the color vocabulary is the stable generic role/scheme set", () => {
  assert.deepEqual([...ColorSchemes], ["light", "dark"]);
  assert.deepEqual(
    [...ColorRoles],
    [
      "background",
      "surface",
      "overlay",
      "foreground",
      "muted",
      "border",
      "primary",
      "primaryForeground",
      "secondary",
      "secondaryForeground",
      "accent",
      "accentForeground",
      "success",
      "warning",
      "danger",
      "info",
    ],
  );
});

test("the scale vocabularies are the stable generic key sets", () => {
  assert.deepEqual(
    [...SpacingSteps],
    ["none", "xs", "sm", "md", "lg", "xl", "2xl", "3xl"],
  );
  assert.deepEqual([...RadiusSteps], ["none", "sm", "md", "lg", "full"]);
  assert.deepEqual([...ShadowSteps], ["none", "sm", "md", "lg"]);
  assert.deepEqual([...BreakpointSteps], ["sm", "md", "lg", "xl"]);
  assert.deepEqual(
    [...FontWeightRoles],
    ["regular", "medium", "semibold", "bold"],
  );
  assert.deepEqual(
    [...MotionDurationSteps],
    ["instant", "fast", "normal", "slow"],
  );
});

test("the base theme provides every color role in every scheme", () => {
  assert.equal(baseTheme.name, "livecore-base");
  for (const scheme of ColorSchemes) {
    const colors = baseTheme.tokens.color[scheme];
    assert.ok(colors, `missing color scheme: ${scheme}`);
    for (const role of ColorRoles) {
      assert.equal(
        typeof colors[role],
        "string",
        `missing color ${scheme}.${role}`,
      );
    }
  }
});

test("the base theme populates every non-color scale completely", () => {
  for (const step of SpacingSteps) {
    assert.equal(typeof baseTheme.tokens.spacing[step], "string");
  }
  for (const step of RadiusSteps) {
    assert.equal(typeof baseTheme.tokens.radius[step], "string");
  }
  for (const step of ShadowSteps) {
    assert.equal(typeof baseTheme.tokens.shadow[step], "string");
  }
  for (const step of BreakpointSteps) {
    assert.equal(typeof baseTheme.tokens.breakpoint[step], "string");
  }
  for (const role of FontWeightRoles) {
    assert.equal(typeof baseTheme.tokens.typography.fontWeight[role], "number");
  }
  for (const step of MotionDurationSteps) {
    assert.equal(typeof baseTheme.tokens.motion.duration[step], "string");
  }
});

test("defineTheme returns the same theme value unchanged", () => {
  const theme = {
    name: "passthrough",
    tokens: baseTheme.tokens,
  };
  assert.equal(defineTheme(theme), theme);
});
