/**
 * Session-event contract drift gate tests for @livecore/contracts (CORE-RT-008).
 *
 * The TypeScript-side mirror of spec-consistency check 11: the published event
 * vocabulary (`KnownSessionEventTypes`) and payload field sets
 * (`KnownSessionEventPayloadFields`) must equal the server's — the non-deferred
 * `csv/event_catalog.csv` events, the `SessionEventTypes.cs` constants and the
 * `SessionEventPayloads.cs` payload records. This file proves the gate REJECTS
 * seeded drift (an extra/missing/renamed event or payload field) over fixtures,
 * then proves the REAL repository tree passes — the same seeded-drift + real-tree
 * pattern as the OpenAPI drift gate and the PowerShell spec-consistency tests.
 *
 * Run with the Node built-in test runner (no new dependency); the package `test`
 * script builds `dist/` first so the real-tree check can read the published
 * contract values.
 */
import assert from "node:assert/strict";
import { test } from "node:test";

import {
  diffEventContract,
  findEventContractDrift,
  parseCatalogEvents,
  parseEmittedEventTypes,
  parsePayloadContracts,
} from "../scripts/event-contract-drift.mjs";

// A small, internally-consistent fixture: one emitted event (with its payload) and
// one deferred catalog row that need not be emitted. Mutating a clone of it seeds
// each kind of drift below.
function consistentFixture() {
  return {
    knownTypes: ["SessionStarted"],
    knownFields: { SessionStarted: ["SessionId", "Status"] },
    catalogEvents: [
      { event: "SessionStarted", isDeferred: false },
      { event: "SceneCreated", isDeferred: true },
    ],
    emittedTypes: ["SessionStarted"],
    serverPayloads: { SessionStarted: ["SessionId", "Status"] },
  };
}

function findingsMatching(findings, pattern) {
  return findings.filter((finding) => pattern.test(finding));
}

test("a contract that matches the catalog, constants and payload records has no findings", () => {
  assert.deepEqual(diffEventContract(consistentFixture()), []);
});

test("a DEFERRED catalog row need not be in the known vocabulary", () => {
  // SceneCreated is deferred in the fixture and absent from knownTypes/emitted — exactly the real
  // SceneCreated / ContentBlockCreated — so it must not be flagged as missing.
  const findings = diffEventContract(consistentFixture());
  assert.equal(findingsMatching(findings, /SceneCreated/).length, 0);
});

test("a seeded EXTRA event in the known vocabulary fails the gate", () => {
  const fixture = consistentFixture();
  fixture.knownTypes.push("GhostEvent");
  fixture.knownFields.GhostEvent = ["SessionId"];
  fixture.serverPayloads.GhostEvent = ["SessionId"]; // isolate the vocabulary drift from payload drift
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /VOCAB: KnownSessionEventTypes lists 'GhostEvent'/,
    ).length > 0,
    "an event the contract knows but the catalog/constants do not must fail",
  );
});

test("a seeded MISSING event (in the catalog and constants, not the contract) fails the gate", () => {
  const fixture = consistentFixture();
  fixture.catalogEvents.push({ event: "ParticipantJoined", isDeferred: false });
  fixture.emittedTypes.push("ParticipantJoined");
  fixture.serverPayloads.ParticipantJoined = ["ParticipantId"];
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /csv\/event_catalog\.csv lists non-deferred event 'ParticipantJoined' but .*KnownSessionEventTypes omits it/,
    ).length > 0,
    "a non-deferred catalog event the contract omits must fail",
  );
  assert.ok(
    findingsMatching(
      findings,
      /SessionEventTypes\.cs emits 'ParticipantJoined' but KnownSessionEventTypes omits it/,
    ).length > 0,
    "an emitted constant the contract omits must fail",
  );
});

test("a seeded RENAMED event fails the gate in both directions", () => {
  const fixture = consistentFixture();
  // Rename only the contract's copy: it now lists a name neither the catalog nor the constants have,
  // and the real name they have is no longer in the contract.
  fixture.knownTypes = ["SessionStartedRenamed"];
  fixture.knownFields = { SessionStartedRenamed: ["SessionId", "Status"] };
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /KnownSessionEventTypes lists 'SessionStartedRenamed'/,
    ).length > 0,
    "the renamed (unknown) name must fail",
  );
  assert.ok(
    findingsMatching(
      findings,
      /non-deferred event 'SessionStarted' but .*KnownSessionEventTypes omits it/,
    ).length > 0,
    "the original name now missing from the contract must fail",
  );
});

test("a seeded EXTRA payload field fails the gate", () => {
  const fixture = consistentFixture();
  fixture.knownFields.SessionStarted = ["SessionId", "Status", "LeakedField"];
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /'SessionStarted' payload field 'LeakedField' is in KnownSessionEventPayloadFields but not in the server/,
    ).length > 0,
    "a payload field the contract adds but the server does not have must fail",
  );
});

test("a seeded MISSING payload field fails the gate", () => {
  const fixture = consistentFixture();
  fixture.knownFields.SessionStarted = ["SessionId"]; // dropped Status
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /'SessionStarted' payload field 'Status' is in the server contract but missing from KnownSessionEventPayloadFields/,
    ).length > 0,
    "a server payload field the contract omits must fail",
  );
});

test("a seeded RENAMED payload field fails the gate in both directions", () => {
  const fixture = consistentFixture();
  fixture.knownFields.SessionStarted = ["SessionId", "Statuss"]; // typo'd Status
  const findings = diffEventContract(fixture);
  assert.ok(
    findingsMatching(
      findings,
      /payload field 'Status' is in the server contract but missing/,
    ).length > 0,
  );
  assert.ok(
    findingsMatching(
      findings,
      /payload field 'Statuss' is in KnownSessionEventPayloadFields but not in the server/,
    ).length > 0,
  );
});

// --- The parsers read the real server sources correctly. -----------------------

test("the catalog parser reads the event names and the deferred flag", () => {
  const rows = parseCatalogEvents(
    "event,emitter,visible_to,persisted,notes\n" +
      "SessionStarted,Host/CoHost,session audience,yes,Starts live run\n" +
      "SceneCreated,Host/CoHost,Host/CoHost,yes,Prep event; DEFERRED - no session scope\n",
  );
  assert.deepEqual(rows, [
    { event: "SessionStarted", isDeferred: false },
    { event: "SceneCreated", isDeferred: true },
  ]);
});

test("the emitted-type parser reads the public const string values", () => {
  const types = parseEmittedEventTypes(
    'public const string SessionStarted = "SessionStarted";\n' +
      'public const string RecapGenerated = "RecapGenerated";\n' +
      "private static readonly HashSet<string> _hostOnly = new() { SessionCreated };\n",
  );
  assert.deepEqual(types, ["SessionStarted", "RecapGenerated"]);
});

test("the payload parser resolves each event to its record's fields", () => {
  const map = parsePayloadContracts(
    "public sealed record SessionLifecycleEventPayload(Guid SessionId, string Status);\n" +
      "public sealed record SceneActivatedEventPayload(Guid SceneId);\n" +
      "public static readonly IReadOnlyDictionary<string, Type> ByEventType = new Dictionary<string, Type>(StringComparer.Ordinal)\n" +
      "{\n" +
      "    [SessionEventTypes.SessionStarted] = typeof(SessionLifecycleEventPayload),\n" +
      "    [SessionEventTypes.SceneActivated] = typeof(SceneActivatedEventPayload),\n" +
      "};\n",
  );
  assert.deepEqual(map, {
    SessionStarted: ["SessionId", "Status"],
    SceneActivated: ["SceneId"],
  });
});

test("the payload parser throws on a mapping to an undefined record", () => {
  assert.throws(
    () =>
      parsePayloadContracts(
        "public static readonly IReadOnlyDictionary<string, Type> ByEventType = new()\n" +
          "{\n" +
          "    [SessionEventTypes.SessionStarted] = typeof(MissingPayload),\n" +
          "};\n",
      ),
    /MissingPayload/,
  );
});

// --- The real repository tree passes the gate (CORE-RT-008). -------------------

test("the real @livecore/contracts session-event contract matches the server", async () => {
  const findings = await findEventContractDrift();
  assert.deepEqual(
    findings,
    [],
    "the published event vocabulary/payloads have drifted from csv/event_catalog.csv, " +
      "apps/api/Realtime/SessionEventTypes.cs and apps/api/Realtime/SessionEventPayloads.cs; " +
      "reconcile packages/contracts/src/events.ts (CORE-RT-008).",
  );
});

test("the real tree exposes exactly the ten emitted events with payload contracts", async () => {
  // A guardrail on the fixtures above: the real surface is the ten non-deferred catalog events, each
  // with a payload field set, so a no-op gate (e.g. empty inputs) cannot pass unnoticed.
  const { KnownSessionEventTypes, KnownSessionEventPayloadFields } =
    await import("../dist/index.js");
  assert.equal(KnownSessionEventTypes.length, 10);
  assert.equal(Object.keys(KnownSessionEventPayloadFields).length, 10);
  for (const eventType of KnownSessionEventTypes) {
    assert.ok(
      Array.isArray(KnownSessionEventPayloadFields[eventType]) &&
        KnownSessionEventPayloadFields[eventType].length > 0,
      `${eventType} must have a non-empty payload field set`,
    );
  }
});
