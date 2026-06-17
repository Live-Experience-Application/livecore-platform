/**
 * Publish-shape / packed-tarball tests for @livecore/ui-core (CORE-PUB-001).
 *
 * The package is published (no longer `private`) so a vertical app in another repo
 * can install it from the registry. These tests assert the package as it would
 * ship:
 *
 *   1. package.json declares a correct, publishable surface — not `private`, with
 *      `publishConfig` (access + registry), `repository`, `license`, `sideEffects`
 *      and the entry points (`main`/`module`/`types`/`exports`).
 *   2. `pnpm pack --json` produces a complete, importable tarball: it contains every
 *      declared entry point, the type declarations, the CHANGELOG and the LICENSE,
 *      and it EXCLUDES tests and non-shipping sources (`src/`, `tsconfig*.json`).
 *
 * The pack runs the same `pnpm` that invoked the test (resolved from `npm_execpath`),
 * packing into a throwaway directory that is removed afterwards, so the working tree
 * stays clean. No new dependency: `pnpm pack --json` reports the file manifest
 * directly, so the test never has to open the gzip/tar itself.
 */
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const PACKAGE_DIR = new URL("../", import.meta.url);
const PACKAGE_PATH = fileURLToPath(PACKAGE_DIR);
const manifest = JSON.parse(
  readFileSync(new URL("package.json", PACKAGE_DIR), "utf8"),
);

const SHIPPED_ROOT_FILES = [
  "package.json",
  "CHANGELOG.md",
  "LICENSE",
  "THIRD-PARTY-NOTICES.md",
];

const stripDot = (specifier) => specifier.replace(/^\.\//, "");

/**
 * Pack the package exactly as it would be published and return the sorted list of
 * POSIX-style paths the tarball contains. Uses `pnpm pack --json`, which reports the
 * packed file manifest without us opening the gzip/tar, and packs into a temporary
 * directory so no `.tgz` is left behind in the working tree.
 */
function packedFilePaths() {
  const dest = mkdtempSync(join(tmpdir(), "livecore-pack-"));
  try {
    const args = ["pack", "--json", "--pack-destination", dest];
    const execpath = process.env.npm_execpath;
    const stdout =
      execpath && /\.(c|m)?js$/i.test(execpath)
        ? execFileSync(process.execPath, [execpath, ...args], {
            cwd: PACKAGE_PATH,
            encoding: "utf8",
          })
        : execFileSync("pnpm", args, {
            cwd: PACKAGE_PATH,
            encoding: "utf8",
            shell: process.platform === "win32",
          });
    const report = JSON.parse(stdout);
    // pnpm reports a single object; npm reports an array of one. Accept both.
    const result = Array.isArray(report) ? report[0] : report;
    return result.files.map((entry) => entry.path.replace(/\\/g, "/")).sort();
  } finally {
    rmSync(dest, { recursive: true, force: true });
  }
}

let packed;
try {
  packed = packedFilePaths();
} catch (cause) {
  throw new Error(
    "Could not `pnpm pack` the package for the publish-shape test. Build the " +
      "package first (`pnpm --recursive run build`) and ensure pnpm is available. " +
      `Underlying error: ${cause.message}`,
  );
}

test("the package declares a publishable surface, not private (CORE-PUB-001)", () => {
  assert.notEqual(
    manifest.private,
    true,
    "the package must not be private so a vertical in another repo can install it",
  );
  assert.equal(manifest.publishConfig?.access, "public");
  assert.equal(typeof manifest.publishConfig?.registry, "string");
  assert.match(manifest.publishConfig.registry, /^https:\/\//);
  assert.equal(manifest.license, "AGPL-3.0-or-later");
  assert.equal(manifest.sideEffects, false);
  assert.ok(manifest.repository, "the package must declare its repository");
  assert.equal(manifest.main, "./dist/index.js");
  assert.equal(manifest.module, "./dist/index.js");
  assert.equal(manifest.types, "./dist/index.d.ts");
  assert.equal(manifest.exports["."].types, "./dist/index.d.ts");
  assert.equal(manifest.exports["."].import, "./dist/index.js");
});

test("the manifest declares engines and repository.directory (CORE-PUB-004)", () => {
  // engines and repository.directory pass through `pnpm pack`/`pnpm publish`
  // verbatim (pnpm rewrites only workspace: deps), so the on-disk manifest is the
  // packed manifest for these fields; the pack test below confirms package.json
  // ships in the tarball.
  assert.equal(
    typeof manifest.engines?.node,
    "string",
    "the package must declare the supported Node range in engines.node",
  );
  assert.match(
    manifest.engines.node,
    />=?\s*\d+/,
    "engines.node must declare a Node version lower bound (e.g. '>=22')",
  );
  assert.equal(
    manifest.repository?.directory,
    "packages/ui-core",
    "repository.directory must point at the package's path in the monorepo",
  );
});

test("the packed tarball contains every declared entry point, types, CHANGELOG and LICENSE (CORE-PUB-001)", () => {
  const required = new Set(
    [
      manifest.main,
      manifest.module,
      manifest.types,
      manifest.exports["."].types,
      manifest.exports["."].import,
      manifest.exports["."].default,
      "CHANGELOG.md",
      "LICENSE",
      "THIRD-PARTY-NOTICES.md",
      "package.json",
    ]
      .filter(Boolean)
      .map(stripDot),
  );
  for (const entry of required) {
    assert.ok(
      packed.includes(entry),
      `the published tarball must contain '${entry}'; packed: ${JSON.stringify(packed)}`,
    );
  }
});

test("the packed tarball excludes tests and non-shipping sources (CORE-PUB-001)", () => {
  for (const path of packed) {
    assert.ok(!path.startsWith("src/"), `source must not ship: ${path}`);
    assert.ok(!path.startsWith("tests/"), `tests must not ship: ${path}`);
    assert.ok(
      !path.startsWith("scripts/"),
      `build/check scripts must not ship: ${path}`,
    );
    assert.ok(
      !/\.test\.[^/]+$/.test(path),
      `a test file must not ship: ${path}`,
    );
    assert.ok(
      !/^tsconfig.*\.json$/.test(path),
      `a tsconfig must not ship: ${path}`,
    );
    assert.ok(
      !path.includes("node_modules/"),
      `node_modules must not ship: ${path}`,
    );
    // Everything that ships comes from the declared file roots only: the built
    // dist/ tree plus the handful of root metadata files.
    const allowed =
      path.startsWith("dist/") || SHIPPED_ROOT_FILES.includes(path);
    assert.ok(allowed, `unexpected file in the published tarball: ${path}`);
  }
});
