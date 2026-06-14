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

## Session-scoped delivery (CORE-SVIS-001)

The recipient set and every visibility decision are bounded by the event's `sessionId`. A reveal is
session-scoped (`docs/adr/0013-session-scoped-visibility-rules.md`, `docs/10_DATABASE_SCHEMA.md`), so a
workspace running several **concurrent** sessions keeps each run's reveals to its own session: a reveal in
session A must never be delivered to — or replayed for — a participant connected to a different concurrent
session B of the same workspace (the cross-session leak; threats T5/T3 in
`docs/07_SECURITY_THREAT_MODEL.md`). Two things enforce this together:

- **Session-keyed groups.** Every group a delivery is addressed to is keyed by the event's session
  (`session:{sessionId}:hosts` / `:observers` / `:participant:{participantId}`), and a connection joins
  only the groups of the session it connected to. So a delivery for session A reaches only connections in
  session A's groups.
- **Session-scoped visibility gate.** The recipient resolver gates every audience and per-recipient
  delivery through the central Visibility engine **bounded by the event's session**, so the decision
  consults only the reveal rules of that session. The participant-visible feed
  (`GET /api/v1/participants/{participantId}/visible-feed`, now scoped by a required `sessionId` query
  parameter) and reconnect replay reuse the same session-scoped decision, so REST feed, live delivery and
  replay can never diverge.

A participant is workspace-scoped and there is no persisted session-participant roster yet (deferred to
the Presence epic), so the audience fan-out still enumerates the workspace's active participants as the
candidate set; the session boundary is enforced by the session-keyed groups and the session-scoped gate
above, not by a roster.

## Per-session event sequence (CORE-RTC-001)

Every session event carries a **per-session, gap-free, strictly monotonic** `sequence` number, and both live
ordering and reconnect replay use **that sequence** — `(session_id, sequence)` — not the UUIDv7 `eventId`.
The id is only monotonic at millisecond resolution, so events appended within one millisecond (a single
reveal publishes `ContentRevealed` + `VisibilityRuleChanged` + `SceneActivated` at the same instant) would
reorder under an id-ordered read; ordering by the sequence preserves their append order. The number is
allocated at append time from a per-session counter inside the command's unit-of-work transaction
(CORE-CONC-002), so the stream is gap-free even under concurrent appends (see
`docs/10_DATABASE_SCHEMA.md`). The sequence travels in every delivered envelope and every replay item, so a
**client detects a missed event as a gap** in the sequence.

## Connection re-authorization and eviction (CORE-RTC-002)

A connection's server-managed groups are resolved **once**, at connect (`RealtimeConnectionResolver` /
`SessionHub.OnConnectedAsync`). Without a re-authorization hook a connection keeps those groups until it
reconnects, so a caller whose standing changes **mid-session** would keep receiving events their old standing
allowed: a removed participant's still-open socket stays in its participant group, and a demoted host keeps
receiving the host/observer group deliveries (the participant audience fan-out is re-gated per event by the
recipient resolver — it enumerates only **active** participants — but group **membership** is not). So a
removal or a role change must re-authorize the live connection, not only the next one.

The Realtime module owns that re-authorization as an **eviction** seam (`IRealtimeConnectionEvictor`, backed by
the `RealtimeConnectionRegistry`):

- **The registry records every admitted connection.** On connect the hub records the connection's
  server-computed authorized facts (its tenant/workspace/session, the subject behind it, and — for a
  participant connection — the participant it owns) together with an in-process abort handle, and clears the
  record on disconnect. The facts come from the resolver's admission, never from client input.
- **Eviction aborts exactly the affected connections.** When a participant is removed (or leaves) the
  Sessions module's leave/remove flow raises `EvictParticipantAsync`, and when a member's workspace role
  changes the role-change command raises `EvictWorkspaceMemberAsync`. The registry aborts the matching
  connections — the removed participant's, or the demoted member's host/observer connections — so their open
  socket is torn down and receives no further events. The match is scoped by the full tenant/workspace/session
  (and participant or subject) tuple, so a connection in another session, workspace or tenant is never touched
  (threats T1/T5). A membership role change never aborts the subject's separate participant connection (their
  participant standing is unchanged), and vice-versa.
- **Eviction only ever removes a connection.** It never adds a connection to a group and never sends an event,
  so it can never widen an audience (threat T3). The authoritative re-admission stays the **same** resolver:
  an evicted client that reconnects is authorized from scratch, so a demoted host re-joins only its new role's
  groups and a removed participant is denied (fail-closed) — the single authorization path, reused, not a
  parallel one.

**Single-instance scope.** The registry holds only the connections of the instance it runs on (the abort
handle is an in-process `HubCallerContext.Abort`), so it evicts a connection on **that** instance immediately.
In a multi-instance deployment a connection held by another instance is evicted when that instance handles the
same command; the always-on backstop remains the per-event recipient computation, which already re-gates the
participant audience fan-out to active participants on every instance. Cross-instance host/observer eviction
(propagating the eviction signal over the backplane) is a documented follow-up — the same single-instance
posture as the in-process backplane above.

## Offline/reconnect

The initial production-ready version supports reconnect replay and local read cache. It does not support full peer-to-peer offline multiplayer.

Reconnect requires:

- last acknowledged **sequence** number (the `afterSequence` cursor; a cursor of N replays N+1.. with no
  skips or duplicates — CORE-RTC-001)
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

**Sticky-session affinity is also required at scale (CORE-DEP-002).** The backplane is necessary but not
sufficient for multiple instances: a SignalR connection starts with a **negotiate** request that issues a
`connectionId`, and the non-WebSocket fallbacks (Server-Sent Events, long polling) then make further HTTP
requests that **must all reach the same instance** that issued it. A multi-instance deployment therefore also
needs **sticky sessions / ARR affinity** at the reverse proxy for the `/hubs` endpoint, alongside the backplane;
the affinity (negotiate/transport handshake) and the backplane (cross-instance fan-out) solve different
problems. The proxy-specific configuration is documented in `docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Graceful
shutdown and SignalR sticky-session affinity").

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
