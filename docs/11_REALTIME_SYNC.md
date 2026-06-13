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

Use a Valkey/Redis-compatible backplane when multiple API instances run.

The Core defines the seam this backplane plugs into: `IRealtimeBackplane` is the single transport boundary a
server-computed event delivery crosses on its way to the connected clients. The `InProcessRealtimeBackplane`
delivers each computed delivery to its server-managed SignalR group over the hub (`IHubContext<SessionHub>`).

For multiple API instances the SignalR backplane itself is made Valkey/Redis-backed (CORE-OPS-007): with a
backplane connection string configured, `AddStackExchangeRedis`
(`Microsoft.AspNetCore.SignalR.StackExchangeRedis`) replaces the in-memory SignalR `HubLifetimeManager` with a
Redis-backed one, so every group send through `IHubContext<SessionHub>` is published over Redis pub/sub and
reaches the connections held by **every** instance — not just the one that computed the delivery. This swaps
only the transport **beneath** `IHubContext`; the `IRealtimeBackplane` seam and the per-recipient recipient
computation are unchanged. The connection string is supplied at runtime via configuration only
(`Realtime:Backplane:ConnectionString`), never in this repository.

**Single-instance constraint when unconfigured.** With no backplane connection string configured the host
keeps the in-memory SignalR backplane, which delivers correctly only for a **single** API instance. A
multi-replica deployment **must** configure a backplane; without one, an event computed on one replica reaches
only the clients connected to that replica and is silently dropped for clients connected to the others. This
is the documented single-instance fallback (see `docs/13_SELF_HOSTING_REQUIREMENTS.md`).

The backplane only ever forwards an already-authorized delivery — one recipient-safe payload to one
server-managed group, computed by the per-recipient recipient resolver. It cannot widen the audience: the
Redis transport carries each already-computed group send verbatim and a connection only ever belongs to a
group the server placed it in (groups are server-managed, never client-chosen). So the recipient computation
stays the single send path and enabling scale-out never leaks a hidden event (threat T3), whether or not a
backplane is configured.

## Tests

- selected participant receives reveal
- unselected participant does not receive reveal
- host receives audit/confirmation event
- reconnect replays only visible events
- stale connection cannot subscribe to another participant feed
