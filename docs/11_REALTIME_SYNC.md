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
session:{sessionId}:audience
session:{sessionId}:observers
```

Every active participant connection joins BOTH its own `session:{sessionId}:participant:{participantId}`
group (for private, selected-participant events) AND the shared `session:{sessionId}:audience` group, so an
audience-wide event the whole audience may see is delivered with a single group send rather than one per
participant (CORE-PERF-001, "Collapsed audience delivery" below).

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
  (`session:{sessionId}:hosts` / `:observers` / `:audience` / `:participant:{participantId}`), and a
  connection joins only the groups of the session it connected to. So a delivery for session A reaches only
  connections in session A's groups — including its shared `:audience` group, which a participant connected
  to a concurrent session B is NOT in.
- **Session-scoped visibility gate.** The recipient resolver gates every audience and per-recipient
  delivery through the central Visibility engine **bounded by the event's session**, so the decision
  consults only the reveal rules of that session. The participant-visible feed
  (`GET /api/v1/participants/{participantId}/visible-feed`, now scoped by a required `sessionId` query
  parameter) and reconnect replay reuse the same session-scoped decision, so REST feed, live delivery and
  replay can never diverge.

A participant is workspace-scoped and there is no persisted session-participant roster yet (deferred to
the Presence epic), so the session's audience is its workspace's active participants — the population each
participant connection is admitted from. The session boundary is enforced by the session-keyed groups (the
shared `:audience` group included) and the session-scoped gate above, not by a roster.

## Participant roster and presence read (CORE-PRS-002)

A vertical UI needs a "who is present" panel, so `GET /api/v1/sessions/{sessionId}/roster` (module
**Realtime**, roles "workspace members") reads a session's participant roster together with each
participant's current presence/connection state. It is a read built **on top of** the existing building
blocks — no parallel roster engine:

- **The roster is the session AUDIENCE.** Because a participant is workspace-scoped and there is no persisted
  session-participant roster (above), the roster is the session's workspace **active participants** — the same
  population each participant connection is admitted from — read through the reused, tenant- AND
  workspace-scoped `IParticipantRepository.ListActiveByWorkspaceAsync`. A soft-removed participant has left
  the audience and is excluded.
- **Presence comes from the realtime connection registry.** Each participant's `present` flag is true iff the
  participant currently holds a live realtime connection to **this** session, read from
  `RealtimeConnectionRegistry.GetConnectedParticipantIds` (matched on the full tenant/workspace/session tuple,
  exactly like eviction; a host/observer member connection carries no participant id and is not counted). The
  registry records the connections **this API instance** holds (the same per-instance record connection
  eviction acts on, "Connection re-authorization and eviction" below). Under scale-out the SignalR backplane
  transports group **sends** across instances but does **not** aggregate a global connection list, so presence
  is reported **per instance**; aggregating it across replicas would need a shared presence store and is a
  documented follow-up. This only ever **under-reports** presence (a participant connected to another replica
  reads as not-present) and never widens authorization.
- **Role-projected, fail-closed, hidden-404.** The read is allowed to any member of the session's workspace
  and is projected by the member's role through the central Visibility role classification
  (`VisibilityRoles`): the host-content roles (Owner/Admin/Host/CoHost) get the full roster **with** the
  host-only participant user-account link, while every other role (Participant/Observer/Auditor) gets the
  host-only-field-**stripped** projection — so a participant sees who is present by display identity but never
  which user backs them (threat T2). The target tenant is the required `?organizationSlug=` query parameter;
  a foreign tenant, an unknown session and a non-member of the session's workspace are all hidden as `404`
  (threats T1/T5), never `403`.

## Collapsed audience delivery (CORE-PERF-001)

An audience-wide reveal used to resolve its recipients by **enumerating the workspace's active participants
and running one visibility-rule lookup per participant**, then **publishing to each participant's group
individually** — `1+N` identical `visibility_rules` lookups and `N` backplane publishes per event (and a
Scene reveal emits two events, doubling both), all awaited inside the host's reveal HTTP request. Reveal
latency and DB/backplane load therefore grew **linearly with audience size**.

The recipient computation now collapses that to a **single rule lookup and a shared group**:

- **One rule lookup, an in-memory gate.** For an audience-wide event the resolver asks the Visibility
  engine to resolve the audience from **one** session-scoped `ListByResourceAsync` lookup
  (`VisibilityPolicy.ResolveAudienceVisibilityAsync`): it decides **in memory** both whether the whole
  audience may see the subject (an audience-wide visible rule) and which participants are entitled **only**
  by a rule scoped to exactly them (a selected-participant reveal on the same resource). No per-participant
  query runs, so the per-reveal query count is **independent of audience size**.
- **One shared-group publish.** When the audience may see the subject, the event is delivered to the
  observers group and the shared `session:{sessionId}:audience` group — **one backplane publish each** —
  reaching every active participant through the shared group instead of `N` per-participant publishes. When
  the audience-at-large may **not** see it but a participant-scoped visible rule exists, the event still
  reaches exactly those participants through their **own** groups (derived from the same single lookup), so
  the selected-participant guarantee is unchanged.
- **Per-participant lookups/groups are reserved for selected-participant events.** A reveal targeted at one
  participant (`TargetParticipantId` set) still uses a single per-participant gate and that participant's own
  group — the path whose cost is inherently `O(1)` in the audience.

**Visibility correctness is unchanged** and fail-closed: the shared `:audience` group is gated by the same
audience-wide decision and only ever carries events the whole audience is entitled to (a private reveal is
never routed there), the observers gate is independent, and the participant-scoped set reaches exactly the
individually-entitled participants — the same recipient set the old per-participant fan-out produced. The
collapsed audience decision reuses the **same** `VisibilityRule` predicates the REST `CanViewResource` /
`CanParticipantViewResource` decisions use, so the realtime recipient set can never diverge from the REST
one (`docs/05_MODULE_CONTRACTS.md`: visibility is decided in one place). Reconnect replay reuses the
recipient resolver unchanged, so a participant — now also a member of the shared `:audience` group — replays
exactly its live audience view.

## Bounded, cursored reconnect replay (CORE-PERF-002)

Reconnect replay used to load the **entire** session stream
(`SessionEventRepository.ListBySessionAsync` — no sequence cursor, no row limit), slice it after the client
cursor **in memory**, and then resolve recipient visibility **per event** (one rule lookup each). A reconnect
to a long, well-attended session therefore did `O(events)` work and rule lookups inside a single request, so a
reconnect storm could become a self-inflicted denial of service (threat T9 in
`docs/07_SECURITY_THREAT_MODEL.md`).

Replay is now **bounded** and **cursored** end to end:

- **The cursor and a row cap are pushed into SQL.** `ISessionEventRepository.ListBySessionAfterAsync` reads
  only the rows whose per-session `sequence` is **greater than the client cursor**, in sequence order, capped
  at a fixed maximum page size (`SessionReplayService.MaxReplayEvents`) — a
  `WHERE sequence > cursor ORDER BY sequence LIMIT cap` backed by the unique `session_events(session_id,
  sequence)` index. The whole stream is never loaded then filtered in memory, so the read cost is bounded
  regardless of stream length.
- **Recipient visibility for the page is resolved in ONE batched lookup.** The recipient resolver's
  `ResolveBatchAsync` collects the page's **distinct** visibility subjects and resolves them with a single
  batched rule query (`VisibilityRuleRepository.ListByResourcesAsync` → `VisibilityPolicy
  .ResolveAudienceVisibilityBatchAsync`), reusing the **same** CORE-PERF-001 in-memory audience gate — so the
  per-replay rule-lookup count is **one**, independent of both the number of events and the audience size
  (replay cost no longer grows with `events × participants`). The per-event routing/projection is the SAME
  `BuildDeliveries` the live single-event path uses, so a replayed page is byte-for-byte what live delivery
  would have produced — **replay correctness and filtering are unchanged** (threat T3).
- **Paging forward never silently drops events.** When a page comes back FULL (exactly the cap), more rows may
  remain, so the response carries `nextSequence` — the page's highest RAW per-session sequence — for the
  client to pass back as `afterSequence` and fetch the next page. It is the raw sequence (independent of which
  events the caller may see), so a client pages forward **even across a full page that contains no event it is
  entitled to**; a non-full page sets `nextSequence` to null (caught up). A sequence number is not sensitive
  content (a host already sees every sequence, and a participant already detects gaps by design — threat T7).

## In-memory participant-visible feed (CORE-PERF-004)

The participant-visible feed (`GET /api/v1/participants/{participantId}/visible-feed`) used to load the
workspace's visibility rules once and then, **for every candidate resource**, ask the policy
`CanParticipantViewResourceAsync` — which issued **another** `ListByResourceAsync` per candidate, re-fetching
rows the feed already held. A workspace with `M` ruled resources therefore did `1+M` `visibility_rules`
lookups inside one request, so a host previewing a busy session paid a query count that grew with the
session's content (threat T9 in `docs/07_SECURITY_THREAT_MODEL.md`).

The feed now computes the participant's visible set from a **single** workspace-rule load
(`VisibilityRuleRepository.ListByWorkspaceAsync`) **gated in memory**: `VisibilityPreviewService` reads the
rules once and `VisibilityPolicy.ComputeVisibleResourcesForParticipant` selects, **over the rows already in
memory**, the distinct resources whose rules of the requested `sessionId` make them visible to the
participant — the SAME aggregate predicate (`VisibilityRule.BelongsToSession` + `IsVisibleTo`) the
per-resource `CanParticipantViewResourceAsync` applies. So the per-feed rule-lookup count is **one**,
independent of resource volume, and the visible set is **byte-for-byte** what the old per-candidate
computation produced — **feed correctness is unchanged** and fail-closed: a Hidden resource, a reveal in a
concurrent session, and a reveal scoped to a different participant are all excluded (the session-scope and
selected-participant guarantees; threats T5/T3). Because the gate reuses the central `VisibilityRule`
predicates, the REST feed can never diverge from per-resource access or the realtime recipient set
(`docs/05_MODULE_CONTRACTS.md`: visibility is decided in one place). The same in-memory gate is the one the
audience-participant entity-search path reuses (CORE-PERF-008, next), so feed and search stay consistent.

## In-memory audience-filtered entity search (CORE-PERF-008)

Entity search (`EntitySearchService`) filters a workspace's entities to what the caller may see: a
host-capable role gets every matching entity, an audience participant gets only the entities revealed to them
**in their session**, and any other caller fails closed to the empty view. The audience path used to mirror
the pre-CORE-PERF-004 feed — for **every candidate entity** it asked the policy
`CanParticipantViewResourceAsync`, which issued **another** `ListByResourceAsync` per candidate — so a search
over `N` candidate entities did `N` `visibility_rules` lookups (the SAME `1+M` fan-out CORE-PERF-004 fixed for
the feed, in a path that story did not name). The path is latent today — there is no entity HTTP route
(deliberately absent per CORE-SPEC-003) — but CORE-ENT-006 (entity list/read) moves the audience-filter
pattern toward a live path, so the cost is fixed before it ships.

The audience path now reuses the **same** single-load in-memory gate the feed uses: it resolves the
participant's visible set through `VisibilityPreviewService.GetVisibleResourcesForParticipantAsync` — one
`ListByWorkspaceAsync` load, then `VisibilityPolicy.ComputeVisibleResourcesForParticipant` selecting the
visible resources **over the rows already in memory** — and narrows the already-scoped candidate set to the
entities in that set. So the per-search rule-lookup count is **one workspace load plus the entity load**,
**independent of entity volume** (`O(1)`, not `O(N)`), and the visible set is **byte-for-byte** what the old
per-candidate computation produced: the audience-wide, selected-participant, cross-session and host
equivalences hold and the filter stays fail-closed — a Hidden rule, a reveal to a different participant and a
reveal in a sibling session all grant nothing (the selected-participant and session-scope guarantees; threats
T5/T3, and the query-volume abuse surface T9 in `docs/07_SECURITY_THREAT_MODEL.md`). Because both paths
resolve visibility through the one gate, **the visible feed and entity search can never diverge** — visibility
is decided in exactly one place (`docs/05_MODULE_CONTRACTS.md`; `docs/02_ARCHITECTURE.md`: entity visibility is
not computed ad hoc in many places). No new schema, route or event: the single-resource read index and the
`ListByWorkspaceAsync` load CORE-PERF-004 already established back this path unchanged.

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
allowed: a removed participant's still-open socket stays in its participant **and shared `:audience`**
groups, and a demoted host keeps receiving the host/observer group deliveries. With the collapsed audience
delivery (CORE-PERF-001) the shared `:audience` group is sent to as a whole, so audience delivery is no
longer additionally re-gated per event by active status — taking a departed participant out of the audience
is the eviction seam's job. So a removal or a role change must re-authorize the live connection, not only the
next one.

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

**Cross-instance eviction (CORE-RES-008).** The abort handle is an in-process `HubCallerContext.Abort`, so each
instance can only abort the connections **it** holds. Without propagation a demoted/removed user keeps a live
socket on **another** replica until it reconnects — and with the collapsed audience delivery (CORE-PERF-001) a
departed participant is taken out of the shared `:audience` group by **eviction**, not by a per-event
active-participant re-gate, so that residual socket would keep receiving audience deliveries it is no longer
entitled to. So after evicting its **own** held connections (always first and unconditionally), the registry
**propagates** the eviction over the deployment's configured Valkey/Redis backplane — the **same**
`Realtime:Backplane:*` connection the realtime fan-out uses — as an opaque eviction descriptor
(`RealtimeConnectionEviction`: the tenant/workspace/session and the participant or subject id, surrogate ids only,
never content — threat T7). Every replica's `RealtimeConnectionEvictionListener` feeds received descriptors into
`RealtimeConnectionRegistry.ApplyRemoteEviction`, which aborts the matching sockets **it** holds, so the
demoted/removed user's socket is torn down on **every** replica within a bounded window. The properties that keep
it fail-closed and unchanged for a single instance:

- **The broadcast can only ever cause *more* eviction, never less.** The local eviction happens first and
  unconditionally, so a dropped/failed broadcast merely falls back to the previous reconnect window — never a
  widened audience and never a stale serve forever. The publish is **best-effort** and never throws, so a
  backplane failure can never turn a successful demotion/removal into a request error (**no new fail-open path**).
- **A received eviction is applied *locally only* and never re-published**, so it cannot echo across the backplane,
  and a malformed/unrecognized descriptor is rejected defensively (it can never abort an unintended connection).
- **Eviction still only ever *removes* a connection** — the authoritative re-admission stays the same resolver — so
  propagating it never widens an audience (threat T3).
- **With no backplane configured** (a single-instance deployment) the no-op eviction backplane is wired and
  behaviour is **exactly as before**: the registry evicts its own held sockets and nothing is broadcast. The
  feature is on by default and can be reverted to that posture with `Realtime:CrossInstanceEviction=false`
  (`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

It is the realtime-connection counterpart of the cross-instance authorization-cache invalidation (CORE-RES-007),
reusing the same backplane and the same fail-closed shape. The session-keyed groups still bound delivery to the
correct session on every instance, and the per-event visibility gate still bounds it to the rules of that session,
so a hidden event is never delivered regardless of instance count.

**Cross-instance eviction is behaviorally tested.** The deterministic two-replica contract (a demotion/removal on
one registry aborts the target socket on a second registry sharing one backplane; an unrelated connection is
untouched; a received eviction is never re-published) is proven by the in-memory unit tests, and the production
path is exercised end-to-end over the **real** Valkey/Redis backplane by `RedisRealtimeConnectionEvictionPropagationTests`
(two API instances sharing one PostgreSQL system of record and the real backplane), which runs only when a backplane
server is configured (the CI `integration-postgres` job, `LIVECORE_TEST_REDIS`); a default local run skips it.

## Offline/reconnect

The initial production-ready version supports reconnect replay and local read cache. It does not support full peer-to-peer offline multiplayer.

Reconnect requires:

- last acknowledged **sequence** number (the `afterSequence` cursor; a cursor of N replays N+1.. with no
  skips or duplicates — CORE-RTC-001)
- server-side replay filter
- duplicate event handling
- **bounded paging** — a reconnect replays at most one capped page; when more remains the response's
  `nextSequence` is the cursor for the next page, so a large backlog is drained over successive bounded
  requests rather than one unbounded load (CORE-PERF-002)

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

**The app fails fast on a multi-instance misconfiguration (CORE-RES-006).** Because the backplane is opt-in,
that single-instance constraint used to fail **silently**: a deployment that ran more than one API instance with
no backplane started cleanly and then dropped cross-instance delivery with no signal. The chart now refuses to
render that topology (CORE-DEP-009), and the **API itself** is the matching defence in depth for any path that
bypasses the chart guard (a `kubectl scale`, a Compose `--scale api=N`, a hand-written manifest). The deployment
declares its instance count in `Realtime:InstanceCount` (`Realtime__InstanceCount`, set to the replica count;
absent or non-positive = a single instance), and a startup guard (`RealtimeBackplaneStartupValidator`, reusing the
`ProductionConfigurationValidator` pure-decision pattern) reads only the **topology** — the declared count and
whether a connection string is present, never the connection string value (threat T7):

- **More than one declared instance with no backplane** is a definite misconfiguration in **any** environment, so
  the host **refuses to start** with a clear, named error (the same fail-fast posture as the OIDC audience guard,
  CORE-OPS-004) rather than serving a broken multi-instance realtime topology.
- **A single declared instance on the in-process backplane in a container/production deployment** is correct now
  but a scale-up foot-gun, so the host starts normally and logs **one prominent startup warning** that
  cross-instance realtime delivery is disabled.
- **A configured backplane** (any instance count) and a **single-instance development run** start **silently** —
  single-instance development is unaffected.

**Sticky-session affinity is also required at scale (CORE-DEP-002).** The backplane is necessary but not
sufficient for multiple instances: a SignalR connection starts with a **negotiate** request that issues a
`connectionId`, and the non-WebSocket fallbacks (Server-Sent Events, long polling) then make further HTTP
requests that **must all reach the same instance** that issued it. The hub keeps the **full transport set** —
`MapRealtimeHubs` (`apps/api/Realtime/RealtimeEndpoints.cs`) maps the hub without forcing `SkipNegotiation` /
WebSockets-only — so the negotiate + fallback handshake is the default, and across replicas it needs affinity.
A multi-instance deployment therefore also needs **sticky sessions / ARR affinity** at the reverse proxy for
the `/hubs` endpoint, alongside the backplane; the affinity (negotiate/transport handshake) and the backplane
(cross-instance fan-out) solve different problems. The proxy-specific configuration is documented in
`docs/13_SELF_HOSTING_REQUIREMENTS.md` ("Graceful shutdown and SignalR sticky-session affinity").

**The in-repo Helm chart wires that affinity for you (CORE-DEP-013).** So the requirement is not left implicit,
the chart's `Ingress` template (`deploy/helm/livecore/templates/ingress.yaml`) **automatically** renders the
nginx cookie-affinity annotations when the `Ingress` is enabled **and** `api.replicaCount > 1` — the same
condition that requires the backplane (CORE-DEP-009). The default single-replica install renders no affinity
annotation, so that path is unaffected; an operator who instead forces a WebSockets-only client transport (no
negotiate fallback) can opt out with `ingress.sessionAffinity.enabled=false`. The two controls (affinity +
backplane) remain both required at scale.

The backplane only ever forwards an already-authorized delivery — one recipient-safe payload to one
server-managed group, computed by the per-recipient recipient resolver. It cannot widen the audience: the
Redis transport carries each already-computed group send verbatim and a connection only ever belongs to a
group the server placed it in (groups are server-managed, never client-chosen). So the recipient computation
stays the single send path and enabling scale-out never leaks a hidden event (threat T3), whether or not a
backplane is configured.

**Cross-instance propagation is behaviorally tested (CORE-TST-003).** The DI wiring (that
`AddStackExchangeRedis` is selected when a connection string is configured) is unit-tested, but the
production HA path itself is exercised end-to-end by `RedisBackplanePropagationTests`: it boots multiple API
instances sharing one PostgreSQL system of record and the **real** Redis/Valkey SignalR backplane, then proves
an event revealed on one instance reaches a client whose live hub connection is held by a **different**
instance, and that a deployment configured with a different `ChannelPrefix` receives nothing (the prefix
namespaces the deployment's pub/sub channels and does not leak). The test runs only when a backplane server is
configured (the CI `integration-postgres` job's Redis/Valkey service, `LIVECORE_TEST_REDIS`); a default local
run skips it. It reuses the unchanged backplane registration and never widens the audience.

## Typed live client and hub contract (CORE-RT-007)

The live path used to exist only as server-side C# constants — the hub route
(`RealtimeHubRoutes.SessionHub` = `/hubs/session`), the SignalR client method
(`SessionEventEnvelope.ClientMethod` = `SessionEvent`) and the `access_token`
query-string auth (`HubBearerToken`) — with no contract mirror or SDK client, so a
vertical's primary reason to build on Core (live reveal delivery over the hub) had to
hand-wire the connection. The contract and the typed SDK now close that gap:

- **`@livecore/contracts`** exports the hub path, the client-method name and the
  connection-parameter shape as stable constants/types: `RealtimeHubPaths.session`,
  `SESSION_EVENT_CLIENT_METHOD`, `REALTIME_ACCESS_TOKEN_QUERY_PARAM` and the
  `SessionHubConnectionParams` shape (`organizationSlug`, `sessionId`, optional
  `participantId` — identifiers only, **never** a group name). A live envelope is the
  same `SessionEventReplayItem` shape reconnect replay returns, exported as
  `LiveSessionEvent`.
- **`@livecore/sdk-ts`** exposes a typed live client, `client.realtime.connect(params,
  onEvent)`. It builds the hub URL from the identifiers, registers the single
  `SessionEvent` client method as **one** handler that delivers
  `SessionEventReplayItem`-shaped envelopes (the same handler a consumer feeds the
  items reconnect replay returns), and **fails closed without an access token** — it
  resolves the token before opening any connection, so an empty token rejects
  client-side and no hub connection is built. The bearer token travels via the
  connection's `accessTokenFactory` (the `access_token` query parameter), refreshed on
  each reconnect, never baked into the composed URL (threat T7).

The SDK stays free of a SignalR dependency: the consumer supplies a
`hubConnectionFactory` (typically wrapping `@microsoft/signalr`), the same injectable
transport seam the REST path uses for `fetch`. Because a client supplies only
identifiers and never a group name, it **cannot select a group or another
participant's feed** — the server resolves the authorized server-managed groups
(CORE-RT-002; threat T3); the SDK is a typed transport, not a security boundary.

## Tests

- selected participant receives reveal
- unselected participant does not receive reveal
- host receives audit/confirmation event
- reconnect replays only visible events
- stale connection cannot subscribe to another participant feed
- the typed live SDK client builds the correct hub URL/params and surfaces a typed
  event stream; it is refused client-side without an access token; and it supplies
  only identifiers, so it cannot select a group or another participant's feed
  (CORE-RT-007)
