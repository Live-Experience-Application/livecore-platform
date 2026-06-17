/**
 * Drift-gate the @livecore/sdk-ts client surface against csv/api_routes.csv
 * (CORE-SDK-007).
 *
 * The typed SDK must expose exactly one client method per documented `/api/v1`
 * route, so the SDK can never silently fall behind the API again (the ~45%
 * coverage regressed silently because nothing enforced parity). The invariant is
 * checked in BOTH directions over the route SET:
 *   - every NON-callback route in `csv/api_routes.csv` must be reachable through a
 *     client method (a new route added without an SDK method fails), and
 *   - every route a client method calls must be a documented non-callback route
 *     (an SDK method calling a phantom or a renamed route fails).
 *
 * Provider-facing webhook callbacks (the routes whose roles cell is
 * `none (provider callback)`, today the Apple/Google store-notification receivers)
 * are EXPLICITLY EXCEPTED: they are called by the store servers, not by a vertical
 * app, so the SDK must NOT expose a method for them — and a method that did would
 * fail the gate.
 *
 * The live realtime client (`client.realtime.connect`) is excepted for free: it
 * opens the `/hubs/session` SignalR hub rather than issuing an `/api/v1` request,
 * so it has no `http.send` route call-site and contributes no route key (the
 * non-route hub surface has its own pinned-constant discipline,
 * docs/23_PACKAGE_VERSIONING.md). A parity check that demanded a route per method
 * would otherwise fail on it.
 *
 * This mirrors the spec-consistency discipline that binds csv/api_routes.csv to the
 * implementation (scripts/LiveCoreSpecConsistency.psm1 check 6, and its OpenAPI
 * analogue check 12) and the TypeScript session-event drift gate (CORE-RT-008): the
 * pure functions are exported so the package test
 * (`tests/sdk-ts.route-parity.test.mjs`) can both assert the REAL tree passes and
 * prove the gate REJECTS seeded drift (a route with no SDK method, an SDK method with
 * no route).
 *
 * Run as a script (`node scripts/route-parity-drift.mjs`, the `check:sdk-parity`
 * package script and the CI `typescript` job step) it reads the real files and exits
 * non-zero on any drift. It reads only source (csv/api_routes.csv and the resource
 * client SOURCES), so it needs no build.
 */
import { readFileSync, readdirSync } from "node:fs";
import { argv } from "node:process";
import { pathToFileURL } from "node:url";

/** The single source of truth for the API routes. */
export const API_ROUTES_URL = new URL(
  "../../../csv/api_routes.csv",
  import.meta.url,
);

/** The resource client sources — every route-issuing client method lives here. */
export const RESOURCES_DIR_URL = new URL("../src/resources/", import.meta.url);

/**
 * The semantic identity of a route: its method plus its path SHAPE, with every
 * path parameter collapsed to a placeholder so `{sessionId}` and `{id}` describe
 * the same route. This is the exact normalization the PowerShell route extractor
 * uses (ConvertTo-LiveCoreRouteKey), so the two gates compare routes identically.
 */
export function routeKey(method, path) {
  const normalized = path.replace(/\{[^}]*\}/g, "{}");
  return `${method.toUpperCase()} ${normalized}`;
}

/**
 * Split one CSV record into fields, honoring RFC 4180 double-quoted fields (so a
 * quoted `roles` cell like `"Owner,Admin"` is one field and the comma-bearing
 * `notes` tail is not mistaken for more columns). A doubled `""` inside a quoted
 * field is an escaped quote.
 */
export function parseCsvRecord(line) {
  const fields = [];
  let current = "";
  let inQuotes = false;
  for (let i = 0; i < line.length; i++) {
    const char = line[i];
    if (inQuotes) {
      if (char === '"') {
        if (line[i + 1] === '"') {
          current += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        current += char;
      }
    } else if (char === '"') {
      inQuotes = true;
    } else if (char === ",") {
      fields.push(current);
      current = "";
    } else {
      current += char;
    }
  }
  fields.push(current);
  return fields;
}

/**
 * Parse `csv/api_routes.csv` into the documented route-key SETS, partitioned by
 * authentication posture. A row whose roles cell begins with `none` is a
 * provider-callback route (the same rule the PowerShell documented-route extractor
 * uses for IsAnonymousDoc); every other row is a consumer route the SDK must cover.
 */
export function parseDocumentedRoutes(csvText) {
  const lines = csvText.split(/\r?\n/);
  const consumer = new Set();
  const callback = new Set();
  for (let i = 1; i < lines.length; i++) {
    const line = lines[i];
    if (line.trim() === "") continue;
    const fields = parseCsvRecord(line);
    const method = (fields[0] ?? "").trim();
    const route = (fields[1] ?? "").trim();
    const roles = (fields[3] ?? "").trim();
    if (method === "" || route === "") continue;
    const key = routeKey(method, route);
    if (/^none\b/i.test(roles)) {
      callback.add(key);
    } else {
      consumer.add(key);
    }
  }
  return { consumer, callback };
}

/**
 * Extract the routes the SDK resource clients call from their SOURCE. Every
 * route-issuing method passes a request spec literal `{ method: "VERB", path: ...
 * }` to `http.send`/`http.sendWithETag`, so the method/path pair is read straight
 * from that literal (mirroring how the PowerShell extractor reads the C#
 * minimal-API registrations). The path is the version-relative literal — a
 * template literal with `${...}` interpolations or a plain string — so each
 * interpolation is collapsed to a placeholder and the `/api/v1` version prefix
 * (which the transport prepends at runtime) is restored before normalization.
 *
 * `sources` is an array of `{ file, text }`. A method with no such request-spec
 * literal (the live `connect`, which opens the hub instead) contributes no route,
 * so the live client is excepted without a special case.
 */
export function parseSdkRoutes(sources) {
  const routes = [];
  const pattern =
    /method:\s*"(GET|POST|PUT|DELETE)"\s*,\s*path:\s*(`[^`]*`|"[^"]*")/g;
  for (const { file, text } of sources) {
    for (const match of text.matchAll(pattern)) {
      const method = match[1];
      const inner = match[2].slice(1, -1);
      const path = "/api/v1" + inner.replace(/\$\{[^}]*\}/g, "{}");
      routes.push({ file, method, path, key: routeKey(method, path) });
    }
  }
  return routes;
}

/**
 * Compare the documented routes to the SDK's route call-sites and return an array
 * of human-readable drift findings (empty when they agree). Both directions are
 * checked: a consumer route with no client method, and a client method whose route
 * is not a documented consumer route (a phantom/renamed route, or a forbidden
 * provider-callback route).
 */
export function diffRouteParity({ documented, sdkRoutes }) {
  const findings = [];
  const sdkKeys = new Set(sdkRoutes.map((route) => route.key));

  for (const key of documented.consumer) {
    if (!sdkKeys.has(key)) {
      findings.push(
        `ROUTE: csv/api_routes.csv route '${key}' has no @livecore/sdk-ts client method (CORE-SDK-007)`,
      );
    }
  }

  const reported = new Set();
  for (const route of sdkRoutes) {
    if (documented.consumer.has(route.key)) continue;
    if (reported.has(route.key)) continue;
    reported.add(route.key);
    if (documented.callback.has(route.key)) {
      findings.push(
        `CALLBACK: @livecore/sdk-ts (${route.file}) exposes a method for '${route.key}', a provider-callback route the SDK must not call (CORE-SDK-007)`,
      );
    } else {
      findings.push(
        `SDK: @livecore/sdk-ts (${route.file}) calls '${route.key}' which is not a route in csv/api_routes.csv (CORE-SDK-007)`,
      );
    }
  }

  return findings;
}

/** Read each resource client source as `{ file, text }`. */
export function readResourceSources() {
  const sources = [];
  for (const entry of readdirSync(RESOURCES_DIR_URL)) {
    if (!entry.endsWith(".ts")) continue;
    const text = readFileSync(new URL(entry, RESOURCES_DIR_URL), "utf8");
    sources.push({ file: `src/resources/${entry}`, text });
  }
  return sources;
}

/**
 * Run the gate against the real repository tree: read csv/api_routes.csv and the
 * resource client sources, and return the documented sets, the SDK route
 * call-sites and the drift findings.
 */
export function findRouteParityDrift() {
  const documented = parseDocumentedRoutes(
    readFileSync(API_ROUTES_URL, "utf8"),
  );
  const sdkRoutes = parseSdkRoutes(readResourceSources());
  const findings = diffRouteParity({ documented, sdkRoutes });
  return { findings, documented, sdkRoutes };
}

function main() {
  const { findings } = findRouteParityDrift();
  if (findings.length > 0) {
    console.error(
      "Coverage parity gate (CORE-SDK-007): @livecore/sdk-ts has drifted from " +
        "csv/api_routes.csv.\n" +
        "The typed SDK must expose exactly one client method per non-callback " +
        "/api/v1 route. Add the missing method, remove the stray one, or " +
        "reconcile csv/api_routes.csv:",
    );
    for (const finding of findings) {
      console.error(`  - ${finding}`);
    }
    process.exit(1);
    return;
  }

  console.error(
    "Coverage parity gate (CORE-SDK-007): @livecore/sdk-ts exposes a client method " +
      "for exactly the non-callback csv/api_routes.csv routes (provider callbacks " +
      "excepted).",
  );
}

// Run only when invoked as a script, so importing the module (the package test
// reuses the pure functions above) has no side effects.
if (argv[1] && import.meta.url === pathToFileURL(argv[1]).href) {
  main();
}
