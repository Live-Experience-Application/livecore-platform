/**
 * Drift-gate the TypeScript session-event contract against the server (CORE-RT-008).
 *
 * The published `@livecore/contracts` session-event surface — the
 * `KnownSessionEventTypes` vocabulary and the `KnownSessionEventPayloadFields`
 * payload field sets — must mirror the server exactly:
 *   - the event NAMES must equal the non-deferred rows of `csv/event_catalog.csv`
 *     and the `public const string` constants of
 *     `apps/api/Realtime/SessionEventTypes.cs` (the emitted vocabulary), and
 *   - each event's payload FIELD SET must equal the identifier-only payload record
 *     mapped to it in `apps/api/Realtime/SessionEventPayloads.cs`.
 *
 * This is the TypeScript-side mirror of the C# catalog-as-contract binding
 * (spec-consistency check 11, CORE-EVT-004): there, the C# `SessionEventTypes`
 * constants are bound to the catalog; here, the published TypeScript contract is
 * bound to the catalog, the C# constants AND the C# payload records, so a vertical
 * switching on known event names and parsing typed payloads can never silently
 * fall out of step with what the server emits (docs/23_PACKAGE_VERSIONING.md;
 * docs/09_EVENT_CATALOG.md).
 *
 * The check logic is exported as pure functions so the package test
 * (`tests/contracts.events.test.mjs`) can both assert the REAL tree passes and
 * prove the gate REJECTS seeded drift (an extra/missing/renamed event or payload
 * field) — the same module + seeded-drift pattern as the OpenAPI drift gate
 * (CORE-OAS-002) and the PowerShell spec-consistency checks.
 *
 * Run as a script (`node scripts/event-contract-drift.mjs`, the `check:events`
 * package script and the CI `typescript` job step) it reads the real files and
 * exits non-zero on any drift.
 */
import { readFileSync } from "node:fs";
import { argv } from "node:process";
import { pathToFileURL } from "node:url";

/** The single source of the session-event vocabulary (the catalog). */
export const CATALOG_URL = new URL(
  "../../../csv/event_catalog.csv",
  import.meta.url,
);

/** The emitted event-type constants (the C# catalog-as-contract). */
export const EVENT_TYPES_URL = new URL(
  "../../../apps/api/Realtime/SessionEventTypes.cs",
  import.meta.url,
);

/** The identifier-only payload records and their event-type mapping. */
export const PAYLOADS_URL = new URL(
  "../../../apps/api/Realtime/SessionEventPayloads.cs",
  import.meta.url,
);

/** The compiled package entry point the published contract values are read from. */
export const CONTRACTS_INDEX_URL = new URL("../dist/index.js", import.meta.url);

/**
 * Parse `csv/event_catalog.csv` into `{ event, isDeferred }` rows. The event name
 * is the first column; a row is deferred when its notes mark it DEFERRED (the same
 * rule the PowerShell catalog extractor uses), so the deferred SceneCreated /
 * ContentBlockCreated are excluded from the emitted vocabulary.
 */
export function parseCatalogEvents(csvText) {
  const lines = csvText.split(/\r?\n/);
  const rows = [];
  for (let i = 1; i < lines.length; i++) {
    const line = lines[i];
    if (line.trim() === "") continue;
    const comma = line.indexOf(",");
    const event = (comma < 0 ? line : line.slice(0, comma)).trim();
    if (event === "") continue;
    rows.push({ event, isDeferred: /\bDEFERRED\b/.test(line) });
  }
  return rows;
}

/**
 * Parse the emitted event-type NAMES from `SessionEventTypes.cs`: the `public
 * const string X = "value"` values. The same regex the PowerShell extractor uses,
 * so the two gates read the catalog-as-contract identically.
 */
export function parseEmittedEventTypes(csharpText) {
  const types = [];
  const pattern = /public\s+const\s+string\s+\w+\s*=\s*"([^"]+)"/g;
  for (const match of csharpText.matchAll(pattern)) {
    types.push(match[1]);
  }
  return types;
}

/**
 * Parse `SessionEventPayloads.cs` into an event-type -> payload field-name map.
 * Reads the `public sealed record Name(params)` declarations for each record's
 * field names (the positional property names) and the `[SessionEventTypes.X] =
 * typeof(Record)` entries of `ByEventType` for the event -> record mapping, then
 * resolves each event to its record's fields. Throws on an inconsistent file (a
 * mapping to an undefined record) — a configuration error, surfaced rather than
 * silently passing.
 */
export function parsePayloadContracts(csharpText) {
  const recordFields = new Map();
  const recordPattern = /public\s+sealed\s+record\s+(\w+)\s*\(([^)]*)\)/g;
  for (const match of csharpText.matchAll(recordPattern)) {
    const recordName = match[1];
    const fields = match[2]
      .split(",")
      .map((parameter) => parameter.trim())
      .filter((parameter) => parameter !== "")
      .map((parameter) => parameter.split(/\s+/).pop());
    recordFields.set(recordName, fields);
  }

  const byEventType = {};
  const mappingPattern =
    /\[\s*SessionEventTypes\.(\w+)\s*\]\s*=\s*typeof\(\s*(\w+)\s*\)/g;
  for (const match of csharpText.matchAll(mappingPattern)) {
    const event = match[1];
    const recordName = match[2];
    const fields = recordFields.get(recordName);
    if (!fields) {
      throw new Error(
        `SessionEventPayloads.ByEventType maps '${event}' to record '${recordName}' which is not declared in SessionEventPayloads.cs`,
      );
    }
    byEventType[event] = fields;
  }
  return byEventType;
}

function difference(a, b) {
  const other = new Set(b);
  return [...new Set(a)].filter((value) => !other.has(value));
}

/**
 * Compare the published TypeScript session-event contract to the server sources
 * and return an array of human-readable drift findings (empty when they agree).
 * Both directions are checked for the vocabulary (vs the catalog AND the emitted
 * constants) and for every payload field set (vs the server payload records), so
 * an extra, missing or renamed event or payload field is reported.
 */
export function diffEventContract({
  knownTypes,
  knownFields,
  catalogEvents,
  emittedTypes,
  serverPayloads,
}) {
  const findings = [];

  const knownSet = new Set(knownTypes);
  const nonDeferred = new Set(
    catalogEvents.filter((row) => !row.isDeferred).map((row) => row.event),
  );
  const emittedSet = new Set(emittedTypes);

  // Vocabulary vs the non-deferred catalog (both directions).
  for (const event of difference([...nonDeferred], [...knownSet])) {
    findings.push(
      `VOCAB: csv/event_catalog.csv lists non-deferred event '${event}' but @livecore/contracts KnownSessionEventTypes omits it (CORE-RT-008)`,
    );
  }
  for (const type of difference([...knownSet], [...nonDeferred])) {
    findings.push(
      `VOCAB: KnownSessionEventTypes lists '${type}' but it is not a non-deferred csv/event_catalog.csv event (CORE-RT-008)`,
    );
  }

  // Vocabulary vs the emitted SessionEventTypes constants (both directions).
  for (const event of difference([...emittedSet], [...knownSet])) {
    findings.push(
      `VOCAB: apps/api/Realtime/SessionEventTypes.cs emits '${event}' but KnownSessionEventTypes omits it (CORE-RT-008)`,
    );
  }
  for (const type of difference([...knownSet], [...emittedSet])) {
    findings.push(
      `VOCAB: KnownSessionEventTypes lists '${type}' but apps/api/Realtime/SessionEventTypes.cs emits no such constant (CORE-RT-008)`,
    );
  }

  // Payload field sets vs the server payload records (both directions, per event).
  const payloadEvents = new Set([
    ...Object.keys(knownFields),
    ...Object.keys(serverPayloads),
  ]);
  for (const event of payloadEvents) {
    const tsFields = knownFields[event];
    const serverFields = serverPayloads[event];
    if (!serverFields) {
      findings.push(
        `PAYLOAD: KnownSessionEventPayloadFields defines '${event}' but SessionEventPayloads.cs maps no payload contract to it (CORE-RT-008)`,
      );
      continue;
    }
    if (!tsFields) {
      findings.push(
        `PAYLOAD: SessionEventPayloads.cs defines a payload contract for '${event}' but KnownSessionEventPayloadFields omits it (CORE-RT-008)`,
      );
      continue;
    }
    for (const field of difference(serverFields, tsFields)) {
      findings.push(
        `PAYLOAD: '${event}' payload field '${field}' is in the server contract but missing from KnownSessionEventPayloadFields (CORE-RT-008)`,
      );
    }
    for (const field of difference(tsFields, serverFields)) {
      findings.push(
        `PAYLOAD: '${event}' payload field '${field}' is in KnownSessionEventPayloadFields but not in the server payload contract (CORE-RT-008)`,
      );
    }
  }

  return findings;
}

/**
 * Run the gate against the real repository tree: read the catalog, the emitted
 * constants and the payload records, read the published contract values from the
 * built package, and return the drift findings.
 */
export async function findEventContractDrift() {
  const catalogEvents = parseCatalogEvents(readFileSync(CATALOG_URL, "utf8"));
  const emittedTypes = parseEmittedEventTypes(
    readFileSync(EVENT_TYPES_URL, "utf8"),
  );
  const serverPayloads = parsePayloadContracts(
    readFileSync(PAYLOADS_URL, "utf8"),
  );

  const { KnownSessionEventTypes, KnownSessionEventPayloadFields } =
    await import(CONTRACTS_INDEX_URL.href);
  const knownFields = Object.fromEntries(
    Object.entries(KnownSessionEventPayloadFields).map(([event, fields]) => [
      event,
      [...fields],
    ]),
  );

  return diffEventContract({
    knownTypes: [...KnownSessionEventTypes],
    knownFields,
    catalogEvents,
    emittedTypes,
    serverPayloads,
  });
}

async function main() {
  const findings = await findEventContractDrift();
  if (findings.length > 0) {
    console.error(
      "Drift gate (CORE-RT-008): the @livecore/contracts session-event contract has " +
        "diverged from the server.\n" +
        "The TypeScript event vocabulary or payload fields no longer match " +
        "csv/event_catalog.csv, apps/api/Realtime/SessionEventTypes.cs and " +
        "apps/api/Realtime/SessionEventPayloads.cs. Reconcile packages/contracts/src/events.ts " +
        "and review the diff as a contract change (docs/23_PACKAGE_VERSIONING.md):",
    );
    for (const finding of findings) {
      console.error(`  - ${finding}`);
    }
    process.exit(1);
    return;
  }

  console.error(
    "Drift gate (CORE-RT-008): the @livecore/contracts session-event vocabulary and payload " +
      "contracts match csv/event_catalog.csv, SessionEventTypes.cs and SessionEventPayloads.cs.",
  );
}

// Run only when invoked as a script, so importing the module (the package test
// reuses the pure functions above) has no side effects.
if (argv[1] && import.meta.url === pathToFileURL(argv[1]).href) {
  await main();
}
