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

The Core defines the seam this backplane plugs into: `IRealtimeBackplane` is the single transport boundary a
server-computed event delivery crosses on its way to the connected clients. The default
`InProcessRealtimeBackplane` delivers to the connections held by one API instance over the SignalR hub; a
multi-instance deployment substitutes a Valkey/Redis-backed implementation so the same delivery also reaches
connections held by other instances. The real backplane package and its configuration belong to deployment,
not to this repository.

The backplane only ever forwards an already-authorized delivery — one recipient-safe payload to one
server-managed group, computed by the per-recipient recipient resolver. It cannot widen the audience, so the
recipient computation stays the single send path and swapping the transport for scale-out never leaks a
hidden event (threat T3).

## Tests

- selected participant receives reveal
- unselected participant does not receive reveal
- host receives audit/confirmation event
- reconnect replays only visible events
- stale connection cannot subscribe to another participant feed
