# Realtime Sync

## Technology

Use SignalR for realtime communication.

## Connection model

Connections join server-managed groups:

```text
org:{organizationId}
workspace:{workspaceId}:hosts
session:{sessionId}:hosts
session:{sessionId}:participant:{participantId}
session:{sessionId}:observers
```

Do not let clients choose arbitrary group names.

## Event delivery

Events are never broadcast blindly.

Flow:

```text
command -> authorize -> persist event -> compute recipients -> project payload -> send to recipient groups
```

## Participant payload projection

Participant events must contain only data visible to that participant.

## Offline/reconnect

The initial production-ready version supports reconnect replay and local read cache. It does not support full peer-to-peer offline multiplayer.

Reconnect requires:

- last acknowledged event ID
- server-side replay filter
- duplicate event handling

## Scale-out

Use Valkey/Redis-compatible backplane later when multiple API instances run.

## Tests

- selected participant receives reveal
- unselected participant does not receive reveal
- host receives audit/confirmation event
- reconnect replays only visible events
- stale connection cannot subscribe to another participant feed
