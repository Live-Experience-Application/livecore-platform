/**
 * Public-surface guard for the worked consumer example (CORE-PUB-003).
 *
 * The required test of this story: the example must compile against the package
 * PUBLIC surfaces, not their internal source, so a breaking change to the published
 * shape fails the example build. Two mechanisms enforce that, and this file proves
 * both stay honest:
 *
 *   1. The `tsc` build of the example (run by the `build`/`test` scripts) type-checks
 *      the example against the packages' published types. Because the example takes
 *      `@livecore/contracts` / `@livecore/sdk-ts` as `workspace:*` dependencies,
 *      module resolution follows each package's `exports`/`types` to its built
 *      `dist/index.d.ts` — the same entry point a registry consumer resolves — and
 *      the packages expose no deep import path, so internal `src/` is unreachable.
 *
 *   2. These tests assert the example keeps importing the Core packages only by their
 *      public entry point (no deep `dist/`/`src/` path, no relative climb into the
 *      package source), declares them as workspace links, and that the compiled
 *      output exposes the worked quickstart against that public surface.
 *
 * Run with the Node built-in test runner (no new dependency), after `tsc` has built
 * `dist/` (the package `test` script does this first).
 */
import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { test } from "node:test";

const srcDir = new URL("../src/", import.meta.url);
const manifest = JSON.parse(
  readFileSync(new URL("../package.json", import.meta.url), "utf8"),
);

// The only Core packages the example may import, each by its bare package entry
// point (the published surface), never a deep `@livecore/<pkg>/dist|src/...` path.
const ALLOWED_LIVECORE_SPECIFIERS = new Set([
  "@livecore/contracts",
  "@livecore/sdk-ts",
]);

/** Every `from "<specifier>"` module specifier in a source file. */
function importSpecifiers(source) {
  const specifiers = [];
  const pattern = /\bfrom\s*["']([^"']+)["']/g;
  let match;
  while ((match = pattern.exec(source)) !== null) {
    specifiers.push(match[1]);
  }
  return specifiers;
}

const sourceFiles = readdirSync(srcDir).filter((file) => file.endsWith(".ts"));

test("the example has source to check", () => {
  assert.ok(
    sourceFiles.length > 0,
    "expected the example to ship TypeScript source under src/",
  );
});

test("the example imports the Core packages only by their public entry point", () => {
  for (const file of sourceFiles) {
    const source = readFileSync(new URL(file, srcDir), "utf8");
    for (const specifier of importSpecifiers(source)) {
      if (
        specifier.startsWith("@livecore/") ||
        specifier.startsWith("@livecore-")
      ) {
        assert.ok(
          ALLOWED_LIVECORE_SPECIFIERS.has(specifier),
          `${file} imports '${specifier}'; a consumer must import a Core package only by its published entry point (no deep dist/ or src/ path).`,
        );
      } else if (specifier.startsWith(".")) {
        assert.ok(
          !specifier.includes(".."),
          `${file} imports '${specifier}'; the example must not reach outside itself into package source.`,
        );
      }
    }
  }
});

test("the example depends on the Core packages as workspace links (the published shape, not source paths)", () => {
  const dependencies = manifest.dependencies ?? {};
  for (const name of ALLOWED_LIVECORE_SPECIFIERS) {
    assert.equal(
      dependencies[name],
      "workspace:*",
      `${name} must be a workspace:* dependency so it resolves the package's published exports/dist surface, not its source.`,
    );
  }
});

test("the compiled example exposes the worked quickstart against the public surface", async () => {
  // Importing the built library entry proves the example compiled against the
  // packages' published types and is loadable with no side effects (it constructs
  // no client and opens no connection until runQuickstart is called).
  const compiled = await import(
    new URL("../dist/index.js", import.meta.url).href
  );
  assert.equal(
    typeof compiled.runQuickstart,
    "function",
    "the compiled example must export runQuickstart()",
  );
  assert.equal(
    typeof compiled.loadQuickstartConfigFromEnv,
    "function",
    "the compiled example must export loadQuickstartConfigFromEnv()",
  );
});
