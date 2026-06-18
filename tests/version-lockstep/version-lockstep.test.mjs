/**
 * Cross-package version lockstep test (CORE-CMP-003).
 *
 * The four published packages (@livecore/contracts, @livecore/sdk-ts,
 * @livecore/design-tokens, @livecore/ui-core) are released together and MUST
 * share one version (docs/23_PACKAGE_VERSIONING.md). Each
 * packages/<name>/tests/<name>.build.test.mjs already checks its OWN version
 * triple (the exported VERSION equals package.json equals its CHANGELOG), but
 * nothing compared the four packages to EACH OTHER or to the root CHANGELOG, so
 * bumping one package to 0.2.0 while the others stayed at 0.1.0 still passed -
 * the lockstep guarantee was weaker than docs/23 mandates.
 *
 * This test closes that gap: it asserts the four packages agree on one
 * well-formed SemVer version across package.json, the exported VERSION runtime
 * constant and the per-package CHANGELOG, and that the root CHANGELOG documents
 * that same shared version. It runs with the Node built-in test runner (no new
 * dependency) against the COMPILED dist/ output the per-package build tests run
 * against, so build the packages first (`pnpm --recursive run build`; the
 * `pnpm run test:versions` script does this for you).
 */
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

// The four published packages, in the order docs/23_PACKAGE_VERSIONING.md lists.
const PACKAGES = ["contracts", "sdk-ts", "design-tokens", "ui-core"];

// A SemVer literal, matching the per-package build and type tests.
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

const REPO_ROOT = new URL("../../", import.meta.url);

function changelogDocumentsVersion(changelog, version) {
  // Plain line-prefix match (no RegExp built from the version) to avoid the
  // incomplete-sanitization shape of escaping only "." in a dynamic pattern.
  const headingLine = `## [${version}]`;
  return changelog.split(/\r?\n/).some((line) => line.startsWith(headingLine));
}

/**
 * Pure lockstep check over already-gathered facts, so the seeded negative tests
 * below can exercise it without mutating the repository. Returns the list of
 * human-readable lockstep violations - empty only when the four packages and the
 * root changelog all agree on one shared, well-formed SemVer version.
 */
function findLockstepViolations({ packages, rootChangelog }) {
  const violations = [];
  const shared = packages.length > 0 ? packages[0].manifestVersion : undefined;

  if (typeof shared !== "string" || !SEMVER.test(shared)) {
    violations.push(
      `the shared package version '${shared}' is not a well-formed SemVer literal`,
    );
  }

  for (const pkg of packages) {
    if (pkg.manifestVersion !== shared) {
      violations.push(
        `@livecore/${pkg.name} package.json version '${pkg.manifestVersion}' is not in lockstep with the shared version '${shared}'`,
      );
    }
    if (pkg.exportedVersion !== pkg.manifestVersion) {
      violations.push(
        `@livecore/${pkg.name} exported VERSION '${pkg.exportedVersion}' does not match its package.json version '${pkg.manifestVersion}'`,
      );
    }
    if (!changelogDocumentsVersion(pkg.changelog, pkg.manifestVersion)) {
      violations.push(
        `@livecore/${pkg.name} CHANGELOG.md has no '## [${pkg.manifestVersion}]' entry`,
      );
    }
  }

  if (
    typeof shared === "string" &&
    !changelogDocumentsVersion(rootChangelog, shared)
  ) {
    violations.push(
      `the root CHANGELOG.md has no '## [${shared}]' entry for the shared version '${shared}'`,
    );
  }

  return violations;
}

async function collectActualFacts() {
  const packages = [];
  for (const name of PACKAGES) {
    const packageDir = new URL(`packages/${name}/`, REPO_ROOT);
    const manifest = JSON.parse(
      readFileSync(new URL("package.json", packageDir), "utf8"),
    );
    const changelog = readFileSync(new URL("CHANGELOG.md", packageDir), "utf8");
    const { VERSION } = await import(new URL("dist/index.js", packageDir).href);
    packages.push({
      name,
      manifestVersion: manifest.version,
      exportedVersion: VERSION,
      changelog,
    });
  }
  const rootChangelog = readFileSync(
    new URL("CHANGELOG.md", REPO_ROOT),
    "utf8",
  );
  return { packages, rootChangelog };
}

let actual;
try {
  actual = await collectActualFacts();
} catch (cause) {
  throw new Error(
    "Could not read the built package output for the version lockstep test. " +
      "Build the packages first: `pnpm --recursive run build`. " +
      `Underlying error: ${cause.message}`,
  );
}

test("the four packages and the root changelog share one well-formed version", () => {
  assert.deepEqual(
    findLockstepViolations(actual),
    [],
    "the four published packages must share one version across package.json, the exported VERSION and every changelog (docs/23_PACKAGE_VERSIONING.md)",
  );
});

test("every published package is covered by the lockstep check", () => {
  assert.deepEqual(
    actual.packages.map((pkg) => pkg.name),
    PACKAGES,
  );
  assert.equal(actual.packages.length, 4);
});

test("a single-package version bump is rejected (seeded drift)", () => {
  // Reproduce the exact gap the per-package tests miss: one package bumped to a
  // new version (its OWN triple stays self-consistent) while the others stay
  // behind. The cross-package check must catch the disagreement.
  const seeded = structuredClone(actual);
  const bumped = "0.2.0";
  assert.notEqual(seeded.packages[1].manifestVersion, bumped);
  seeded.packages[1].manifestVersion = bumped;
  seeded.packages[1].exportedVersion = bumped;
  seeded.packages[1].changelog = `## [${bumped}] - 2026-01-01\n`;

  const violations = findLockstepViolations(seeded);
  assert.ok(
    violations.some(
      (v) => v.includes("@livecore/sdk-ts") && v.includes("not in lockstep"),
    ),
    `a one-package version bump must fail the lockstep check; got: ${JSON.stringify(violations)}`,
  );
});

test("an exported VERSION drifting from its manifest is rejected (seeded drift)", () => {
  const seeded = structuredClone(actual);
  seeded.packages[0].exportedVersion = "9.9.9";
  const violations = findLockstepViolations(seeded);
  assert.ok(violations.some((v) => v.includes("exported VERSION")));
});

test("a package changelog missing the shared version is rejected (seeded drift)", () => {
  const seeded = structuredClone(actual);
  seeded.packages[2].changelog = "# Changelog\n\nNo entries yet.\n";
  const violations = findLockstepViolations(seeded);
  assert.ok(violations.some((v) => v.includes("CHANGELOG.md has no")));
});

test("a root changelog missing the shared version is rejected (seeded drift)", () => {
  const seeded = structuredClone(actual);
  seeded.rootChangelog = "# Changelog\n\nNo entries yet.\n";
  const violations = findLockstepViolations(seeded);
  assert.ok(violations.some((v) => v.includes("root CHANGELOG.md")));
});
