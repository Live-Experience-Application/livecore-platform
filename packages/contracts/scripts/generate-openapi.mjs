/**
 * Generate (and drift-check) the OpenAPI-derived TypeScript types for
 * `@livecore/contracts` (CORE-OAS-002).
 *
 * The committed OpenAPI 3 document `openapi/livecore-v1.json` is emitted from the
 * running minimal-API route table and drift-gated against the registered routes
 * (CORE-OAS-001). This module turns that document into the package's generated
 * `src/openapi.ts` types with `openapi-typescript`, so the published TypeScript
 * contract surface is derived from — and can never silently diverge from — the
 * server's own contract.
 *
 * Two modes, sharing one deterministic code path so the committed file and the
 * regenerated output can be byte-compared:
 *   --write  (default)  overwrite src/openapi.ts with the freshly generated types
 *   --check             regenerate in memory and fail (exit 1) on any difference
 *
 * The drift check (`check:openapi`) runs in the CI `typescript` job and in the
 * package build test, so a server contract change that is not reflected in the
 * committed types fails the build. The comparison is line-ending insensitive: the
 * repository stores text as LF (.gitattributes `* text=auto`) but a Windows
 * working copy may check the file out as CRLF, and a line-ending difference is not
 * a contract change.
 *
 * No timestamp or other non-deterministic value is written, so the same document
 * always produces byte-identical output for a pinned `openapi-typescript`.
 */
import { readFileSync, writeFileSync } from "node:fs";
import { argv } from "node:process";
import { pathToFileURL } from "node:url";

import openapiTS, { astToString } from "openapi-typescript";

/** The committed OpenAPI document this package's types are derived from. */
const OPENAPI_DOCUMENT_URL = new URL(
  "../../../openapi/livecore-v1.json",
  import.meta.url,
);

/** The generated TypeScript types written into the package source. */
export const GENERATED_TYPES_URL = new URL(
  "../src/openapi.ts",
  import.meta.url,
);

/**
 * Fixed banner prepended to the generated file. It carries no timestamp (which
 * would break the byte-for-byte drift gate) and tells a reader the file is
 * generated, how to regenerate it and that it is excluded from Prettier/ESLint.
 */
const BANNER = `/**
 * GENERATED FILE - DO NOT EDIT BY HAND.
 *
 * OpenAPI-derived TypeScript types for the LiveCore Core API (CORE-OAS-002),
 * generated from the committed OpenAPI 3 document openapi/livecore-v1.json (itself
 * generated from the running minimal-API route table, CORE-OAS-001).
 *
 * Regenerate after an intentional API/schema change with:
 *   pnpm --filter @livecore/contracts run generate
 *
 * A CI drift gate (check:openapi, run in the typescript job) regenerates these
 * types and fails on any diff, so the published contract surface can never
 * silently diverge from the server's OpenAPI document. The curated, hand-written
 * DTOs in this package are validated against these generated schemas by the
 * contract type-tests (tests/contracts.types.test.ts); a generated breaking change
 * is still a SemVer event in the changelog (docs/23_PACKAGE_VERSIONING.md).
 *
 * Product-neutral: carries only generic Core vocabulary (AGENTS.md). Excluded from
 * Prettier and ESLint because its formatting is owned by the generator.
 */
`;

/** Normalize line endings so the drift check is insensitive to CRLF vs LF. */
function toLf(text) {
  return text.replace(/\r\n/g, "\n");
}

/**
 * Produce the generated TypeScript types as a canonical LF string, terminated by
 * exactly one trailing newline. The same function backs --write, --check and the
 * package build test, so the committed file is exactly what a regeneration emits.
 */
export async function generateOpenApiTypes() {
  const document = JSON.parse(readFileSync(OPENAPI_DOCUMENT_URL, "utf8"));
  const ast = await openapiTS(document);
  const body = toLf(astToString(ast)).replace(/\n+$/, "");
  return `${BANNER}\n${body}\n`;
}

async function main() {
  const mode = process.argv.includes("--check") ? "check" : "write";
  const generated = await generateOpenApiTypes();

  if (mode === "write") {
    writeFileSync(GENERATED_TYPES_URL, generated);
    console.log(
      "Generated packages/contracts/src/openapi.ts from openapi/livecore-v1.json.",
    );
    return;
  }

  let committed;
  try {
    committed = readFileSync(GENERATED_TYPES_URL, "utf8");
  } catch {
    console.error(
      "Drift gate (CORE-OAS-002): packages/contracts/src/openapi.ts is missing. " +
        "Run `pnpm --filter @livecore/contracts run generate` and commit the result.",
    );
    process.exit(1);
    return;
  }

  if (toLf(committed) !== generated) {
    console.error(
      "Drift gate (CORE-OAS-002): packages/contracts/src/openapi.ts is out of date " +
        "with openapi/livecore-v1.json.\n" +
        "The server's OpenAPI document and the published TypeScript contracts have " +
        "diverged. Run `pnpm --filter @livecore/contracts run generate`, review the " +
        "diff as a contract change (docs/23_PACKAGE_VERSIONING.md) and commit it.",
    );
    process.exit(1);
    return;
  }

  console.log(
    "Drift gate (CORE-OAS-002): packages/contracts/src/openapi.ts matches openapi/livecore-v1.json.",
  );
}

// Run only when invoked as a script (`node scripts/generate-openapi.mjs ...`), so
// importing the module (the package build test reuses generateOpenApiTypes) has no
// side effects.
if (argv[1] && import.meta.url === pathToFileURL(argv[1]).href) {
  await main();
}
