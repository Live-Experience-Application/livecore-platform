/**
 * pack:local — build and pack the four @livecore packages to dist/ for LOCAL
 * consumption by a downstream vertical (CORE-DXL-002).
 *
 * Mirror of the `images:local` convenience for the container side (CORE-DXL-001):
 * a downstream vertical that wants to run an UNRELEASED Core through its own
 * end-to-end test harness needs the four published TypeScript packages as
 * installable tarballs, before any release is cut and WITHOUT a registry publish.
 * This script makes that one step:
 *
 *   pnpm run pack:local
 *
 * It runs `pnpm --recursive run build`, then `pnpm pack` for each of the four
 * published packages (contracts, sdk-ts, design-tokens, ui-core) and writes the
 * four `.tgz` tarballs into `dist/` (already gitignored). Each tarball is exactly
 * the surface a registry consumer would resolve — the same `pnpm pack` the publish
 * path uses, so the public entry points, the exported VERSION and PACKAGE_NAME and
 * the declared `files` are intact — at the CURRENT package version, unchanged. The
 * version is deliberately NOT bumped here: a real version bump is the normal
 * lockstep release-adoption flow (docs/23_PACKAGE_VERSIONING.md), not part of the
 * fast local inner loop. A consumer pinning the current version keeps its lockstep
 * guard green.
 *
 * The script is READ-ONLY with respect to the tracked tree: it writes only into the
 * gitignored `dist/` directory (override with LIVECORE_PACK_DEST) and `pnpm pack`
 * does not mutate any on-disk package.json (the `workspace:*` → version rewrite for
 * @livecore/sdk-ts → @livecore/contracts happens inside the tarball only). It adds
 * no publish/release step, so the released npm publish path is unchanged.
 */
import { execFileSync } from "node:child_process";
import { mkdirSync, readdirSync, rmSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = resolve(SCRIPT_DIR, "..");

/** The four published packages, in the docs/23_PACKAGE_VERSIONING.md lockstep order. */
export const PACKED_PACKAGES = [
  "contracts",
  "sdk-ts",
  "design-tokens",
  "ui-core",
];

/** Tarballs are written here; default is the gitignored repo-root `dist/`. */
const DEST = process.env.LIVECORE_PACK_DEST
  ? resolve(process.env.LIVECORE_PACK_DEST)
  : join(REPO_ROOT, "dist");

/**
 * Run the same pnpm that invoked us (resolved from npm_execpath when present, as
 * the per-package pack tests do, so the pinned Corepack pnpm is used), falling back
 * to `pnpm` on PATH. Returns captured stdout when `capture` is set, else inherits.
 */
function pnpm(args, { cwd = REPO_ROOT, capture = false } = {}) {
  const stdio = capture ? ["ignore", "pipe", "inherit"] : "inherit";
  const execpath = process.env.npm_execpath;
  if (execpath && /\.(c|m)?js$/i.test(execpath)) {
    return execFileSync(process.execPath, [execpath, ...args], {
      cwd,
      stdio,
      encoding: "utf8",
    });
  }
  return execFileSync("pnpm", args, {
    cwd,
    stdio,
    encoding: "utf8",
    shell: process.platform === "win32",
  });
}

// Build every workspace package so each package's `dist/` (the only thing `files`
// ships) is current before it is packed.
console.log("pack:local — building all packages (pnpm --recursive run build)…");
pnpm(["--recursive", "run", "build"]);

// Write into a clean destination so the result is exactly the four tarballs: drop
// any stale livecore-*.tgz from a previous run (the gitignored dist/ is ours to
// manage), then ensure the directory exists.
mkdirSync(DEST, { recursive: true });
for (const name of readdirSync(DEST)) {
  if (/^livecore-.*\.tgz$/.test(name)) {
    rmSync(join(DEST, name), { force: true });
  }
}

const packed = [];
for (const pkg of PACKED_PACKAGES) {
  const cwd = join(REPO_ROOT, "packages", pkg);
  // `pnpm pack --json` packs exactly the publish surface and reports the written
  // tarball path, without us opening the gzip/tar. --pack-destination keeps the
  // tarball in dist/ rather than the package directory, so nothing lands in a
  // tracked location.
  const stdout = pnpm(["pack", "--json", "--pack-destination", DEST], {
    cwd,
    capture: true,
  });
  const report = JSON.parse(stdout);
  // pnpm reports a single object; npm reports an array of one. Accept both.
  const result = Array.isArray(report) ? report[0] : report;
  packed.push({ pkg, name: result.name, version: result.version });
  console.log(`pack:local — packed @livecore/${pkg}@${result.version}`);
}

console.log("");
console.log(
  `pack:local — wrote ${packed.length} tarball(s) to ${DEST} at the current, unchanged version(s):`,
);
for (const { pkg, version } of packed) {
  console.log(`  @livecore/${pkg}@${version}`);
}
console.log(
  "These are the published surface a registry consumer resolves; no version was bumped and no file was published or committed.",
);
