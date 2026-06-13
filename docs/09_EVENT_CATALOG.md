# Event Catalog

Session events are append-only and drive live state reconstruction.

## Event principles

Each event has:

- `eventId`
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
separately in `csv/entitlement_event_catalog.csv`. Not every catalog event is
emitted yet: the events wired into the implementation today are
`SessionStarted`, `SessionEnded`, `ParticipantJoined`, `ParticipantLeft`,
`SceneActivated`, `VisibilityRuleChanged`, `ContentRevealed` and
`ContentHidden` (`SessionEventTypes`); the remaining rows are planned and land
with their own stories.

| Event | Emitted by | Visible to | Persisted | Notes |
|---|---|---|---:|---|
| SessionCreated | Host/Admin | Host/Admin/CoHost | yes | not always participant-visible |
| SessionStarted | Host/CoHost | session audience | yes | starts live timeline |
| SessionEnded | Host/CoHost | session audience | yes | ends live timeline |
| ParticipantJoined | Participant/System | Host/CoHost, maybe audience | yes | join visibility configurable |
| ParticipantLeft | System | Host/CoHost | yes | participant feed optional |
| SceneCreated | Host/CoHost | Host/CoHost | yes | preparation event |
| SceneActivated | Host/CoHost | authorized audience | yes | scene switch; emitted on a scene reveal (CORE-EVT-003), subject-gated |
| ContentBlockCreated | Host/CoHost | Host/CoHost | yes | prep only by default |
| VisibilityRuleChanged | Host/CoHost | Host/CoHost/Audit | yes | security-relevant; realtime event subject-gated, host-facing on a hide (CORE-EVT-003), distinct from the audit record |
| ContentRevealed | Host/CoHost | selected recipients | yes | central event |
| ContentHidden | Host/CoHost | selected recipients | yes | un-reveal; inverse of ContentRevealed |
| PrivateMessageSent | Host/CoHost | selected recipients | yes | content filtered per recipient |
| AssetRevealed | Host/CoHost | selected recipients | yes | signed URL requested separately |
| SessionNoteCreated | Host/CoHost | Host/CoHost | yes | never participant-visible by default |
| RecapGenerated | Host/System | Host/CoHost/Admin | yes | participant recap requires separate reveal |

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
4. query events after last acknowledged event
5. filter each event through Visibility module
6. send only projected recipient-safe event payloads

## Event schema versioning

Breaking event payload changes require a new schema version.
