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

| Event | Emitted by | Visible to | Persisted | Notes |
|---|---|---|---:|---|
| SessionCreated | Host/Admin | Host/Admin/CoHost | yes | not always participant-visible |
| SessionStarted | Host/CoHost | session audience | yes | starts live timeline |
| SessionEnded | Host/CoHost | session audience | yes | ends live timeline |
| ParticipantJoined | Participant/System | Host/CoHost, maybe audience | yes | join visibility configurable |
| ParticipantLeft | System | Host/CoHost | yes | participant feed optional |
| SceneCreated | Host/CoHost | Host/CoHost | yes | preparation event |
| SceneActivated | Host/CoHost | authorized audience | yes | scene switch |
| ContentBlockCreated | Host/CoHost | Host/CoHost | yes | prep only by default |
| VisibilityRuleChanged | Host/CoHost | Host/CoHost/Audit | yes | security-relevant |
| ContentRevealed | Host/CoHost | selected recipients | yes | central event |
| ContentHidden | Host/CoHost | selected recipients | yes | un-reveal; inverse of ContentRevealed |
| PrivateMessageSent | Host/CoHost | selected recipients | yes | content filtered per recipient |
| AssetRevealed | Host/CoHost | selected recipients | yes | signed URL requested separately |
| SessionNoteCreated | Host/CoHost | Host/CoHost | yes | never participant-visible by default |
| RecapGenerated | Host/System | Host/CoHost/Admin | yes | participant recap requires separate reveal |

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
