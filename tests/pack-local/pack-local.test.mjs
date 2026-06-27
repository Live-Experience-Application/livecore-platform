/**
 * Local-consumption pack tests for `pnpm run pack:local` (CORE-DXL-002).
 *
 * A downstream vertical that runs an UNRELEASED Core through its own end-to-end
 * test harness consumes the four published @livecore packages as tarballs, before
 * any release is cut and without a registry publish. `pnpm run pack:local` builds
 * and packs the four packages (contracts, sdk-ts, design-tokens, ui-core) into
 * `dist/` (gitignored) at the current, unchanged version. These tests assert that
 * convenience end to end:
 *
 *   1. it emits exactly four `dist/*.tgz`, one per published package, and dist/ is
 *      gitignored so the tarballs are not tracked;
 *   2. each tarball carries the CURRENT package version unchanged (the on-disk
 *      package.json version, e.g. 0.5.0) — so a consumer pinning that version keeps
 *      its lockstep guard green — and its public entry exports VERSION + PACKAGE_NAME;
 *   3. installing the four tarballs into a throwaway consumer resolves @livecore/*
 *      by package specifier to the packed content (including @livecore/sdk-ts
 *      resolving its @livecore/contracts dependency to the packed sibling); and
 *   4. the pack modifies no tracked working-tree file (git status is unchanged).
 *
 * The pack runs the real `scripts/pack-local.mjs` into the real, gitignored dist/
 * (so test 4 is meaningful — a vacuous pack into a private temp dir could never
 * dirty the tree). The throwaway consumer and the tarball extraction happen in an
 * OS temp directory, so the working tree stays clean. No new dependency: the test
 * uses the Node built-in test runner and the system `tar` to extract.
 */
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { test } from "node:test";

// The four published packages, in docs/23_PACKAGE_VERSIONING.md lockstep order.
const PACKAGES = ["contracts", "sdk-ts", "design-tokens", "ui-core"];

const REPO_ROOT = new URL("../../", import.meta.url);
const REPO_ROOT_PATH = fileURLToPath(REPO_ROOT);
const DIST_DIR = join(REPO_ROOT_PATH, "dist");

/** The current, on-disk shared version every tarball must keep unchanged. */
const EXPECTED_VERSION = JSON.parse(
  readFileSync(new URL("packages/contracts/package.json", REPO_ROOT), "utf8"),
).version;

function git(args) {
  return execFileSync("git", args, {
    cwd: REPO_ROOT_PATH,
    encoding: "utf8",
  });
}

/**
 * Run `pnpm run pack:local` into the real dist/ and gather the produced state:
 * the git porcelain status before and after (so the no-tracked-change invariant
 * can be asserted), the tarballs written, and — after extracting all four into a
 * throwaway consumer's node_modules — the surface a consumer resolves per package.
 */
function packLocalAndCollect() {
  const before = git(["status", "--porcelain"]);

  // Run the same pnpm that invoked this test (Corepack-pinned via npm_execpath),
  // exactly as `pnpm run pack:local` would, so the real script path is exercised.
  const execpath = process.env.npm_execpath;
  const runArgs = ["run", "pack:local"];
  if (execpath && /\.(c|m)?js$/i.test(execpath)) {
    execFileSync(process.execPath, [execpath, ...runArgs], {
      cwd: REPO_ROOT_PATH,
      stdio: "inherit",
    });
  } else {
    execFileSync("pnpm", runArgs, {
      cwd: REPO_ROOT_PATH,
      stdio: "inherit",
      shell: process.platform === "win32",
    });
  }

  const after = git(["status", "--porcelain"]);

  const tarballs = readdirSync(DIST_DIR)
    .filter((name) => /^livecore-.*\.tgz$/.test(name))
    .sort();

  // Extract every tarball into a throwaway consumer's node_modules/@livecore/<name>
  // (tar strips the leading package/ directory), i.e. install the packed tarballs
  // into a consumer, then resolve them by package specifier from a probe module.
  const consumer = mkdtempSync(join(tmpdir(), "livecore-pack-consumer-"));
  const tarballByPackage = {};
  for (const pkg of PACKAGES) {
    const tarball = tarballs.find((name) =>
      name.startsWith(`livecore-${pkg}-`),
    );
    if (!tarball) continue;
    tarballByPackage[pkg] = tarball;
    const target = join(consumer, "node_modules", "@livecore", pkg);
    mkdirSync(target, { recursive: true });
    execFileSync(
      "tar",
      ["-xzf", join(DIST_DIR, tarball), "-C", target, "--strip-components=1"],
      { stdio: "inherit" },
    );
  }

  // A throwaway consumer probe that resolves each package by its bare specifier
  // against the extracted node_modules — the real resolution a vertical performs.
  writeFileSync(
    join(consumer, "package.json"),
    JSON.stringify({ name: "livecore-pack-local-consumer", private: true }),
  );
  const probePath = join(consumer, "probe.mjs");
  writeFileSync(
    probePath,
    PACKAGES.map((pkg, i) => `import * as p${i} from "@livecore/${pkg}";`).join(
      "\n",
    ) +
      "\nexport const surfaces = {\n" +
      PACKAGES.map(
        (pkg, i) =>
          `  "${pkg}": { VERSION: p${i}.VERSION, PACKAGE_NAME: p${i}.PACKAGE_NAME },`,
      ).join("\n") +
      "\n};\n",
  );

  return { before, after, tarballs, tarballByPackage, consumer, probePath };
}

let collected;
let resolvedSurfaces;
try {
  collected = packLocalAndCollect();
  ({ surfaces: resolvedSurfaces } = await import(
    pathToFileURL(collected.probePath).href
  ));
} catch (cause) {
  throw new Error(
    "Could not run `pnpm run pack:local` and resolve the packed tarballs in a " +
      "throwaway consumer. Install dependencies first (`pnpm install`). " +
      `Underlying error: ${cause.message}`,
  );
}

test("pack:local emits exactly four dist/*.tgz, one per published package (CORE-DXL-002)", () => {
  assert.equal(
    collected.tarballs.length,
    4,
    `expected four livecore-*.tgz in dist/, got: ${JSON.stringify(collected.tarballs)}`,
  );
  for (const pkg of PACKAGES) {
    assert.ok(
      collected.tarballByPackage[pkg],
      `dist/ must contain a tarball for @livecore/${pkg}; got: ${JSON.stringify(collected.tarballs)}`,
    );
  }
});

test("the dist/ tarballs land in gitignored space (untracked, not staged)", () => {
  for (const tarball of collected.tarballs) {
    const ignoredBy = git(["check-ignore", join("dist", tarball)]).trim();
    assert.ok(
      ignoredBy.length > 0,
      `dist/${tarball} must be gitignored so pack:local commits no file`,
    );
  }
});

test("each tarball keeps the current package version unchanged and names the right package (CORE-DXL-002)", () => {
  for (const pkg of PACKAGES) {
    const target = join(collected.consumer, "node_modules", "@livecore", pkg);
    const manifest = JSON.parse(
      readFileSync(join(target, "package.json"), "utf8"),
    );
    assert.equal(
      manifest.name,
      `@livecore/${pkg}`,
      `the packed @livecore/${pkg} manifest must keep its name`,
    );
    assert.equal(
      manifest.version,
      EXPECTED_VERSION,
      `the packed @livecore/${pkg} must carry the current version ${EXPECTED_VERSION} unchanged (a consumer pinning it keeps its lockstep guard green)`,
    );
  }
});

test("installing the tarballs in a throwaway consumer resolves @livecore/* to the packed content, with VERSION + PACKAGE_NAME intact (CORE-DXL-002)", () => {
  for (const pkg of PACKAGES) {
    const surface = resolvedSurfaces[pkg];
    assert.ok(
      surface,
      `the throwaway consumer must resolve @livecore/${pkg} from the packed tarball`,
    );
    assert.equal(
      surface.PACKAGE_NAME,
      `@livecore/${pkg}`,
      `@livecore/${pkg}'s public entry must export PACKAGE_NAME`,
    );
    assert.equal(
      surface.VERSION,
      EXPECTED_VERSION,
      `@livecore/${pkg}'s public entry must export VERSION ${EXPECTED_VERSION}`,
    );
  }
});

test("pack:local is read-only: it modifies no tracked working-tree file (CORE-DXL-002)", () => {
  // The tarballs go to the gitignored dist/, so the porcelain status the pack saw
  // before it ran must equal the status after — pack introduced no tracked change.
  assert.equal(
    collected.after,
    collected.before,
    "pnpm run pack:local must not change any tracked file (git status must be unchanged); " +
      "the tarballs belong in the gitignored dist/",
  );
});

test.after(() => {
  // The throwaway consumer is ours; remove it. The dist/ tarballs are the intended,
  // gitignored output of pack:local and are left in place.
  if (collected?.consumer) {
    rmSync(collected.consumer, { recursive: true, force: true });
  }
});
