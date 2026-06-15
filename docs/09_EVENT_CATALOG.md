# Event Catalog

Session events are append-only and drive live state reconstruction.

## Event principles

Each event has:

- `eventId`
- `sequence` (per-session, gap-free, strictly monotonic; the ordering/replay key — CORE-RTC-001)
- `organizationId`
- `workspaceId`
- `sessionId`
- `eventType`
- `createdBy`
- `createdAt`
- `payload`
- `visibilityProjection`
- `schemaVersion`

## Core events

The source of truth for the session-event vocabulary is
`csv/event_catalog.csv`; the table below mirrors it (see
`docs/24_SPEC_CONSISTENCY.md`). The store/entitlement domain events live
separately in `csv/entitlement_event_catalog.csv`. The catalog is a **contract,
not aspirational** (CORE-EVT-004, the session-event analogue of CORE-SPEC-002):
the emitted set is the ten names in `apps/api/Realtime/SessionEventTypes.cs` —
`SessionCreated`, `SessionStarted`, `SessionEnded`, `ParticipantJoined`,
`ParticipantLeft`, `SceneActivated`, `VisibilityRuleChanged`, `ContentRevealed`,
`ContentHidden` and `RecapGenerated` — and the spec-consistency check (check 11)
requires that set to equal the **non-deferred** catalog, so the catalog can no
longer list a session event that no command emits. `SceneCreated` and
`ContentBlockCreated` stay in the catalog marked **deferred**: a scene/content
block is workspace-prepared and carries **no session**, so it cannot be a
session-scoped event until a session binds it (the Sessions active-scene
pointer), and emitting it as a session event would need that future story, not
this one. The three vertical/future events `PrivateMessageSent`, `AssetRevealed`
and `SessionNoteCreated` were **removed** from the catalog (they tie to no Core
command and belong to a vertical), so the catalog now lists only events that are
emitted or explicitly deferred.

| Event | Emitted by | Visible to | Persisted | Notes |
|---|---|---|---:|---|
| SessionCreated | Host/Admin | Host/Admin/CoHost | yes | not always participant-visible; emitted host-only on session create (CORE-EVT-004) |
| SessionStarted | Host/CoHost | session audience | yes | starts live timeline |
| SessionEnded | Host/CoHost | session audience | yes | ends live timeline |
| ParticipantJoined | Participant/System | Host/CoHost, maybe audience | yes | join visibility configurable |
| ParticipantLeft | System | Host/CoHost | yes | participant feed optional |
| SceneCreated | Host/CoHost | Host/CoHost | yes | preparation event; **deferred** — no session scope (owner: the Sessions active-scene pointer) |
| SceneActivated | Host/CoHost | authorized audience | yes | scene switch; emitted on a scene reveal (CORE-EVT-003), subject-gated |
| ContentBlockCreated | Host/CoHost | Host/CoHost | yes | prep only by default; **deferred** — no session scope (owner: the Sessions active-scene pointer) |
| VisibilityRuleChanged | Host/CoHost | Host/CoHost/Audit | yes | security-relevant; realtime event subject-gated, host-facing on a hide (CORE-EVT-003), distinct from the audit record |
| ContentRevealed | Host/CoHost | selected recipients | yes | central event |
| ContentHidden | Host/CoHost | selected recipients | yes | un-reveal; inverse of ContentRevealed |
| RecapGenerated | Host/System | Host/CoHost/Admin | yes | participant recap requires separate reveal; emitted host-only by the recap worker (CORE-EVT-004) |

## Scene and visibility lifecycle events (CORE-EVT-003)

`SceneActivated` and the realtime `VisibilityRuleChanged` are emitted by the reveal/hide
commands whenever a command actually changes a resource's visibility (the same change
signal the audit record uses, so a retry or no-op emits nothing):

- Revealing a resource emits `ContentRevealed` (central, CORE-RT-003), `VisibilityRuleChanged`
  (this story), and — when the resource is a `Scene` — `SceneActivated` (revealing a scene
  to the audience is the documented scene switch; there is no separate active-scene command).
- Hiding a resource emits `ContentHidden` (CORE-RT-003) and `VisibilityRuleChanged`.

Each of these events **concerns a governed resource**, so it carries that resource as its
**visibility subject** (the `visibilityProjection` input) and the recipient resolver
(CORE-RT-004) gates delivery through the central Visibility engine: the hosts always receive
it and the audience receives it only when they may see the resource. Consequently a
`VisibilityRuleChanged` for a **hide** (whose subject is now hidden) reaches the hosts only —
the security-relevant, host-facing case — while a participant never receives an event about a
resource they may not see. The realtime `VisibilityRuleChanged` is **distinct from** the
append-only audit record of the same name (one is live-state delivery, the other a security
record). `ContentHidden` deliberately carries **no** subject and is routed by coarse target so
the audience that must remove the resource is still reached.

## Reconnect replay

On reconnect:

1. authenticate connection
2. resolve organization/workspace/session context
3. resolve participant identity if applicable
4. query events after the last acknowledged **sequence** (the cursor is the per-session sequence number,
   not the event id — CORE-RTC-001): a cursor of N returns N+1.. with no skips or duplicates
5. filter each event through Visibility module
6. send only projected recipient-safe event payloads

Because the per-session sequence is gap-free and strictly monotonic, a client orders the stream by it and
detects a missed event as a gap in the sequence.

## Event schema versioning

Breaking event payload changes require a new schema version.
