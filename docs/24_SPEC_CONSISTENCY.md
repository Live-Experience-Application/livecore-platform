# Specification Consistency

This note records the **single source of truth per concern** for the Core
specification, so the route, table, event and epic specs agree with each other
and with the implementation (CORE-DOC-001). When a documented item is not in the
implementation, it is listed here as **deferred** rather than left to drift.

The machine-checkable parts of this agreement are enforced by
`scripts/spec-consistency.ps1` (run locally or in CI alongside the boundary
scan). The check logic lives in `scripts/LiveCoreSpecConsistency.psm1` and is
exercised by `scripts/test-spec-consistency.ps1` (seeded-drift tests), the same
module + test pattern as the other `scripts/` gates.

Since CORE-SPEC-001 the check validates **semantics, not just names**: as well as
the original name-set membership invariants it cross-checks the specs against the
implementation — `csv/api_routes.csv` against the routes the minimal-API
registrations actually mount (both directions), the documented roles/auth against
the `MembershipRole` vocabulary and the `AllowAnonymous` endpoints, the mobile
store CSV against the `MobileApiGateway` route table, and `csv/database_tables.csv`
plus its promised unique indexes against the EF Core model snapshot. So a green
"Spec consistency passed" now also means no undocumented endpoint, no auth-role
drift, no dead/ill-formed store event and no schema/index drift slipped through.

## Sources of truth

| Concern | Single source of truth | Mirrors / derived views |
| --- | --- | --- |
| API routes | `csv/api_routes.csv` (mounted `/api/v1` routes) | `docs/08_API_CONTRACTS.md` representative list; `csv/mobile_store_api_routes.csv` (mobile-facing `/v1` path shape of the store/entitlement routes) |
| Database tables | `csv/database_tables.csv` (matches the EF Core model) | `docs/10_DATABASE_SCHEMA.md` table list; `csv/entitlement_database_tables.csv` (entitlement/store ownership view) |
| Session events | `csv/event_catalog.csv` | `docs/09_EVENT_CATALOG.md` table; `apps/api/Realtime/SessionEventTypes.cs` (the emitted set, bound to the non-deferred catalog by check 11 — CORE-EVT-004) |
| Store/entitlement domain events | `csv/entitlement_event_catalog.csv` | — |
| Epics & stories | `csv/core_epics_stories.csv` (Phase 1) + `csv/core_phase2_epics_stories.csv` (Phase 2) | `docs/18_EPICS_AND_STORIES.md` |

A "mirror" must agree with its source of truth. A representative list (for
example the route block in `docs/08`) may be a **subset**, but it may never
contain an entry absent from its source of truth.

## Reconciliation performed (CORE-DOC-001)

- **Routes.** `GET /api/v1/scenes/{sceneId}` (flagged as docs-only) is now in
  both `docs/08` and `csv/api_routes.csv` and is implemented
  (`apps/api/Scenes/SceneEndpoints.cs`). The implemented
  `PUT /api/v1/workspaces/{workspaceId}` (workspace rename, CORE-WS-003) was
  missing from `csv/api_routes.csv` and has been added.
- **Tables.** The `docs/10` inline list was stale (missing the export, recap,
  entitlement, quota and store tables); it now mirrors `csv/database_tables.csv`
  and the EF Core model (33 tables). `csv/entitlement_database_tables.csv` was
  missing the implemented `plan_entitlements` (added) and listed two tables that
  do not exist in the schema (`purchase_providers`, `billing_account_links`),
  now marked **DEFERRED**.
- **Events.** `docs/09` listed `SceneCreated` but `csv/event_catalog.csv` did
  not; `SceneCreated` has been added to the CSV so the two agree (it remains a
  planned, not-yet-emitted preparation event, like `ContentBlockCreated`).
- **Epics.** `docs/18` mixed the Phase 2 epic *Production Operations Readiness*
  into the Phase 1 list and omitted the other Phase 2 epics. It now lists
  Phase 1 (15 epics, matching `csv/core_epics_stories.csv`) and Phase 2
  (8 epics, matching `csv/core_phase2_epics_stories.csv`) separately.

## Decision recorded (CORE-MON-001): billing in scope for Core v1 (reverses CORE-DOC-002)

CORE-DOC-002 previously **formally deferred** the purchase-to-entitlement grant
chain (and `billing_account_links`) to post-v1, on the basis that billing was
out of scope for Core v1. The product now **requires** monetization in v1, so
CORE-MON-001 reverses that decision. This section is the **single source of truth
for the v1 monetization scope and acceptance**; `docs/01`, `docs/21`, `docs/22`,
`README.md` and the entitlement/store/ad story rows of
`csv/core_epics_stories.csv` defer to it.

**Decision: billing and the purchase-to-entitlement grant chain are IN SCOPE for
Core v1.** Completing the monetization loop is built in v1, not deferred. What v1
must now deliver (previously deferred by CORE-DOC-002):

- the `billing_account_links` table (store-account-to-subject link) that links a
  verified purchase to the authenticated buyer (CORE-MON-002, **implemented** — the
  table exists in the schema, unique on `purchase_transaction_id` so a receipt maps
  to exactly one subject);
- the product → plan → entitlement mapping that turns a verified purchase into a
  plan grant (CORE-MON-003);
- the trigger that wires purchase verification (CORE-STORE-003/004) and store
  notifications (CORE-STORE-005, CORE-JOB-003) to
  `SubjectEntitlementAssignmentService.AssignFromPlanAsync`/`RevokeAsync`
  (CORE-MON-003/004).

The v1 monetization foundation already shipped (CORE-DOC-002 confirmed it is
intact and in scope) and is reused, not rebuilt:

- the provider-neutral verify-and-record gate — a verified purchase is persisted
  as an idempotent, auditable `purchase_transactions`/`purchase_events` record
  (CORE-STORE-001..004);
- the idempotent store-notification → purchase-status pipeline and its
  reconciliation job, which keep the persisted purchase status (the server-side
  source of truth) convergent (CORE-STORE-005, CORE-JOB-003);
- the reusable `SubjectEntitlement` assignment/lookup primitive and server-side
  quota enforcement (CORE-ENTL-001..004), and the entitlement-driven ad
  eligibility read (CORE-ADS-001).

**v1 monetization acceptance.** Monetization is done for Core v1 when:

- a verified, buyer-linked purchase grants the buyer the mapped
  `SubjectEntitlement`, idempotently (a retry or duplicate notification does not
  double-grant), and the grant shows up in the effective-entitlements read;
- a refund, cancellation or chargeback revokes or downgrades the granted
  entitlement and stays revoked (a revoked state is terminal — a later renewal
  cannot resurrect it);
- free-tier quotas (`workspace.active.max`, `session.active.max`,
  `session.participant.max`, `asset.storage.bytes.max`) are enforced
  server-side and cannot be bypassed by clients;
- user-visible premium state comes only from server entitlements; an
  unverified/failed purchase grants nothing (fail-closed).

This is a scope decision consistent with the updated `docs/01`, not an
architecture change, so it is recorded here rather than in an ADR (ADR 0010
already records Core-owned entitlements/quotas and ADR 0011 that mobile ads stay
outside Core). The delivering stories are the Monetization v1 epic
(CORE-MON-001..010) in `csv/core_phase3_epics_stories.csv`; CORE-MON-001 (this
spec reversal) unblocks CORE-MON-002..010. Core stays product-neutral and still
never processes payments, renders a paywall/store or displays ads.

**Schema note (CORE-MON-002 landed `billing_account_links`).** `billing_account_links`
is now **in the implemented schema**: CORE-MON-002 added the table (the
verified-purchase-to-buyer-subject link, unique on `purchase_transaction_id`), so it
appears in `csv/database_tables.csv` and the `docs/10` table list, and its
`csv/entitlement_database_tables.csv` row is **no longer marked DEFERRED**. The
spec-consistency check (which requires every *non-deferred* entitlement table to exist
in the schema) stays green because the table now exists in both places.

**Grant note (CORE-MON-003 implemented the grant chain).** The product → plan →
entitlement grant chain is now **implemented**: a verified, buyer-linked purchase
grants the buyer the mapped `SubjectEntitlement` (the first v1 monetization
acceptance bullet above), idempotently, and the grant shows up in the
effective-entitlements read. CORE-MON-003 **adds no new table** — it composes the
existing `plan_definitions`/`plan_entitlements`/`subject_entitlements` model,
mapping a purchase's `product_reference` to a generic plan by the plan's stable key
and reusing `SubjectEntitlementAssignmentService.AssignFromPlanAsync` — so
`csv/database_tables.csv`, the `docs/10` table list and the spec-consistency check
are unchanged. The remaining v1 acceptance bullet (refund/cancellation revocation
staying revoked) is CORE-MON-004, below.

**Revoke note (CORE-MON-004 made the purchase status machine monotonic).** The
second v1 monetization acceptance bullet above — "a refund, cancellation or
chargeback revokes or downgrades the granted entitlement and **stays revoked** (a
revoked state is terminal — a later renewal cannot resurrect it)" — is now
**implemented**. The purchase status machine is **monotonic**: the revoked states
(`Refunded`, `Cancelled`) are **absorbing** (`PurchaseTransaction.ChangeStatus` /
`PurchaseTransactionStatusMachine`), so a later-occurring renewal can never flip a
refunded purchase back to `Active`, neither on the synchronous webhook nor through
reconciliation — reconciliation re-derives a purchase's status by a **monotonic
fold** over all its recorded notifications in event-time order, not just the
latest one. When a notification drives a purchase into a revoked state the granted
`SubjectEntitlement` is **revoked** (`PurchaseEntitlementRevocationService`, the
inverse of the CORE-MON-003 grant chain, reusing
`SubjectEntitlementAssignmentService.RevokeAsync`), so premium disappears from the
effective-entitlements read and stays gone. CORE-MON-004 **adds no new table** —
it composes the existing `purchase_transactions`/`purchase_events`,
`billing_account_links` and `subject_entitlements` model — so
`csv/database_tables.csv`, the `docs/10` table list and the spec-consistency check
are unchanged. It also hardens the store-notification `OccurredAt` (rejecting a
defaulted/absurd-past or implausible-future event time), since that timestamp is
the ordering key reconciliation derives a purchase's converged status from. With
CORE-MON-004 the v1 monetization acceptance is complete.

**Quota note (CORE-MON-005/006 enforce the remaining free-tier caps).** The third
v1 acceptance bullet above — free-tier quotas "are enforced server-side and cannot
be bypassed by clients" — is now realized for every listed key: CORE-ENTL-004
enforced `workspace.active.max` (workspace create) and `session.active.max`
(session start/end), CORE-MON-005 added `session.participant.max` on the
participant-join/leave path (the `Session` quota subject), and CORE-MON-006 added
`asset.storage.bytes.max` on the asset upload-intent/delete path (the asset's
`Workspace` quota subject). Each reuses the atomic `QuotaEnforcementService`
(CORE-CONC-004) and is fail-closed; none adds a new key, table, route or migration
(the keys were already in the "Generic entitlement keys" list and
`csv/mobile_entitlement_catalog.csv`, and usage is tracked in the existing
`quota_usage` table), so `csv/database_tables.csv`, the `docs/10` table list and
the spec-consistency check are unchanged.

**Adapter-contract note (CORE-MON-008 made the receipt-verification contract
explicit).** The verify gate already delegated Apple/Google receipt verification
to a deployment-supplied adapter behind a fail-closed port (CORE-STORE-001/003/004)
and shipped no provider keys. CORE-MON-008 makes the adapter contract **explicit**
for the two security properties such a seam must guarantee: **sandbox/production
separation** — a `VerifiedPurchase` now carries the verified `PurchaseEnvironment`
and the fail-closed `PurchaseEnvironmentPolicy` makes a **production** deployment
honor only a `Production` purchase (a sandbox receipt is `422`, recorded/granted
nothing — "a sandbox receipt is not honored in production") — and **receipt-replay
protection** (the adapter rejects an already-consumed proof; Core's recording stays
idempotent on `(provider, provider_transaction_id)`). The cryptographic verification
itself stays adapter-supplied (out of Core per threat T7 / `docs/13`). CORE-MON-008
**adds no new table, route or migration** — the environment is consumed by the
honoring gate **before** a purchase is recorded, so a recorded purchase is always
one the deployment honors and no schema column is needed; it reuses the existing
verifier ports and `purchase_transactions`/`purchase_events` model — so
`csv/database_tables.csv`, the `csv/api_routes.csv` route list, the `docs/10` table
list and the spec-consistency check are unchanged.

**Mobile path-shape note (CORE-MON-009 made the mobile `/v1` shape resolve in-process).** The store and
entitlement routes are documented in their **mobile-facing path shape** under a bare `/v1` prefix in
`csv/mobile_store_api_routes.csv` (a mirror, per the sources-of-truth table above), while every Core endpoint
is mounted under the `/api/v1` prefix `docs/08_API_CONTRACTS.md` mandates. Before CORE-MON-009 a mobile client
following a documented `/v1/...` path literally would `404`, because no endpoint was mounted there and there
was no in-repo rewrite. CORE-MON-009 closes that gap **in-process**: the mobile API gateway
(`apps/api/Hosting/MobileApiGateway.cs`) rewrites a request whose path matches one of the documented mobile
routes from its `/v1` path to the corresponding `/api/v1` path **before routing**, so the documented mobile
path reaches the implemented endpoint (no `404`, no external proxy rewrite required) and
`csv/mobile_store_api_routes.csv` now accurately describes a resolvable surface. It is a pure, **scoped**
addressing alias — only the exact routes in `csv/mobile_store_api_routes.csv` are rewritten (any other
`/v1/...` path still `404`s, so the rest of the API is never aliased under a second prefix), the target
endpoint's authentication and server-side tenant/subject authorization run unchanged, and it **adds no
`/api/v1` route, table, event or migration**. So `csv/api_routes.csv` (which lists the mounted `/api/v1`
routes), the `docs/08` representative block, `csv/database_tables.csv`, the `docs/10` table list and the
spec-consistency check are all unchanged, and the check stays green. The gateway's route table is the in-code
mirror of `csv/mobile_store_api_routes.csv`, which stays the single source of truth for the mobile path shapes.

**Atomicity note (CORE-MON-010 made store-notification handling atomic).** The synchronous webhook handler
(`StoreNotificationService.HandleAsync`) now applies a notification's purchase **status change** and writes its
**dedup-ledger row** (`store_notification_events`) in **one database transaction**, reusing the CORE-CONC-002
`TransactionalUnitOfWork`. Before CORE-MON-010 the status change committed first (its own `SaveChanges` via
`PurchaseTransactionService.ChangeStatusAsync`) and only **then** the ledger row was inserted, in **separate**
transactions — so a crash between them left the status applied but the notification **unrecorded**, and the
store's at-least-once **re-delivery** re-applied it, which could **double-append** the `purchase_events` audit
trail. Wrapping both — and, for a revoking notification, the entitlement revocation that precedes them — in one
transaction makes a part-way failure roll **everything** back, so a re-delivery either finds the ledger row and
is a deduplicated no-op or replays the whole effect from scratch: never a status applied without its first-arrival
record, never a duplicated audit entry. The dedup fast-path read stays **outside** the transaction (the unique
`store_notification_events(provider, provider_notification_id)` index is the real race guard, inside). It
**adds no route, table, event or migration** — it only changes a transactional boundary — so the
spec-consistency check stays green. With CORE-MON-010 the Monetization v1 epic (CORE-MON-001..010) is complete.
The reconciliation candidate scan still computes the latest-per-purchase **client-side**
(`ReconcilablePurchaseReader`); a SQL window-function form for high-volume deployments stays a documented
follow-up, fine for this off-by-default, low-volume job.

**Cancelled-semantics note (CORE-MON-011 locked in immediate-permanent revoke).** The v1 acceptance above already
requires that a cancellation "revokes or downgrades the granted entitlement and **stays revoked** (a revoked state
is terminal — a later renewal cannot resurrect it)". CORE-MON-011 makes the **`Cancelled`** semantics an
**explicit contract** and pins them with a test: `Cancelled` is a **termination, not a grace period** — it revokes
the granted entitlement **immediately** (not at period end) and is **absorbing like `Refunded`**, so a `Cancelled`
purchase that later receives a later-`OccurredAt` `Renewed` stays `Cancelled` with the entitlement revoked. This
records the **product decision (2026-06-15): keep immediate-permanent revoke** — a subscriber toggling auto-renew
off loses access immediately and permanently, and a resubscribe is a **new** purchase, not a reactivation of the
absorbed one. It is **test + docs only** (no behavior change from CORE-MON-004) and **adds no table, route, event
or migration**, so the spec-consistency check stays green. The full contract is documented in `docs/21`
("Cancelled means immediate, permanent (absorbing) revoke").

**Cross-product revocation note (CORE-MON-012 narrowed the refund revoke to the entitlements no active purchase still
grants).** The v1 acceptance above requires that a refund/cancellation "revokes or downgrades the granted
entitlement". But a subject can hold **two** purchases whose plans **share** an entitlement (for example two products
that both grant `ads.disabled`), and because a subject holds each entitlement at most once, the original revoke —
which stripped **all** of a refunded product's plan entitlements — also stripped a shared entitlement the subject's
**other still-active** purchase legitimately granted (an under-granting bug). CORE-MON-012 narrows it: before
revoking, `PurchaseEntitlementRevocationService` reads the **same** subject's other linked purchases, keeps only the
**non-revoked** ones other than the purchase being revoked, and passes their product references to
`ProductEntitlementGrantService.RevokeForProductAsync`, which **retains** any entitlement those products still grant —
so refunding one of two products sharing an entitlement **keeps** it, and refunding the **last** product holding it
finally revokes it. Retention is **subject-scoped** (a different subject's active purchase never retains this
subject's entitlement — fail-closed isolation) and **idempotent**. It **adds no new table** — it composes the
existing `billing_account_links` + `purchase_transactions` + `subject_entitlements` model — and its only schema change
is the **non-unique** subject lookup index `ix_billing_account_links_subject_type_subject_id` that serves the new
per-subject read (`docs/10`, `docs/21`).

## Catalog-as-contract note (CORE-SPEC-002 backed the entitlement/store event catalog with real audit actions)

`csv/entitlement_event_catalog.csv` marked eight events `persisted=true, audit=true`
(`EntitlementGranted`/`EntitlementRevoked`, `QuotaExceeded`, the three
`PurchaseVerification*` and the two `StoreNotification*`), but `AuditAction`
(`apps/api/Audit/AuditAction.cs`) carried only the eleven generic Core actions —
none entitlement/store/purchase — and `QuotaExceeded` existed only as an HTTP
helper name. The catalog was therefore **aspirational**, and the spec-consistency
check did not catch it. CORE-SPEC-002 closes that gap:

- the eight catalog `audit=true` events now have a real `AuditAction` member, and
  each is **emitted** as a genuine append-only audit fact on the action it names —
  `EntitlementGranted`/`EntitlementRevoked` by `ProductEntitlementGrantService`,
  `QuotaExceeded` at the quota-denial sites (workspace create, session
  create/start, participant join, asset upload-intent), the
  `PurchaseVerification*` trio by the Apple/Google verification endpoints, and the
  `StoreNotification*` pair by `StoreNotificationService`;
- the spec-consistency **check 8** now binds the catalog to the enum (`audit=true`
  **iff** a matching `AuditAction` member exists), so the catalog can no longer
  drift back to aspirational without failing CI;
- a purchase and the entitlement it grants are **deployment-spanning, not
  tenant-scoped** (a user's premium follows the user's purchase, not an
  organization; `purchase_transactions` has no `organization_id` — docs/21), so the
  grant/revoke, purchase-verification and store-notification facts are recorded as
  **platform-level** audit facts: `audit_logs.organization_id` is now **nullable**
  (the only schema change, ADR 0014), and such facts are append-only but stand
  **outside** the per-tenant tamper-evident hash chain (CORE-SEC-003), whose spine
  is the per-tenant append sequence — the same append-only posture the established
  `purchase_events` monetization trail has. `QuotaExceeded` is a normal
  tenant-scoped fact (it is denied inside an already tenant-scoped command). The
  tenant-scoped audit reads filter by a concrete organization, so a platform fact
  is never returned through any tenant's id (threat T5). It adds **no route, table
  or event** — only the nullable column — so the other spec-consistency checks are
  unchanged.

## Session-event catalog-as-contract note (CORE-EVT-004 made the session-event catalog real)

`csv/event_catalog.csv` and `docs/09` listed fifteen session events while only
eight were emitted (the names in `apps/api/Realtime/SessionEventTypes.cs`): the
catalog was **aspirational**, the session-event analogue of the entitlement/store
gap CORE-SPEC-002 closed. CORE-EVT-004 makes it a **contract**:

- the two formerly-unemitted events that tie to an existing Core command are now
  **emitted** — `SessionCreated` host-only on session create (appended in the
  create command's unit of work, CORE-CONC-002, and delivered after commit), and
  `RecapGenerated` host-only by the background recap worker (appended to the
  recap's session stream when a recap is produced). Both are **host-only** events
  (`SessionEventTypes.IsHostOnly`): the recipient resolver delivers them to the
  session hosts only — never an observer or participant — live and on reconnect
  replay, so a created session or a generated recap never leaks to the audience
  (the catalog's "not always participant-visible" / "participant recap requires
  separate reveal"; threats T2/T7);
- the two **workspace-prepared** events `SceneCreated`/`ContentBlockCreated` are
  marked **deferred** in the catalog: they carry no session, so they cannot be
  session-scoped events in the per-session `session_events` stream until a session
  binds the scene/content block (the Sessions active-scene pointer, the named
  owner), and making `session_events.session_id` optional would be an architecture
  change (an ADR) out of scope here;
- the three vertical/future events `PrivateMessageSent`, `AssetRevealed` and
  `SessionNoteCreated` were **removed** from `csv/event_catalog.csv` and `docs/09`
  (they tie to no Core command and belong to a vertical);
- the spec-consistency **check 11** now binds the catalog to `SessionEventTypes`
  (the emitted set equals the **non-deferred** catalog, both directions), so the
  catalog can no longer drift back to aspirational without failing CI.

It **adds no route, table or migration** — only the two new event-type constants,
their emission and the host-only routing class — so the other spec-consistency
checks are unchanged.

## Genuinely deferred items

These are documented for design intent but are **not** in the implemented
Core v1 schema/behavior. They are not drift; they are explicit deferrals or
in-scope-for-v1 items not yet built (noted per item).

- **`purchase_providers`** — provider handling is in-code (the purchase-provider
  abstraction, CORE-STORE-001), not a database table. Marked DEFERRED in
  `csv/entitlement_database_tables.csv`.
- **Deferred session events** — `SceneCreated` and `ContentBlockCreated` are in
  the catalog but **deferred** (CORE-EVT-004): a scene/content block is
  workspace-prepared and carries no `session_id`, so it cannot be a session-scoped
  event in the per-session `session_events` stream until a session binds it (the
  **Sessions active-scene pointer**, a future story, is the named owner). They are
  marked `DEFERRED` in `csv/event_catalog.csv` so the spec-consistency check
  (check 11) excludes them from the emitted-set comparison. The other five
  formerly-unemitted events were resolved by CORE-EVT-004: `SessionCreated` and
  `RecapGenerated` are now emitted (the emitted set is the ten names in
  `apps/api/Realtime/SessionEventTypes.cs`), and `PrivateMessageSent`,
  `AssetRevealed` and `SessionNoteCreated` were removed from the catalog as
  vertical/future events with no Core command.
- **Deliberately-absent capabilities and the authorization model** — the
  read/CRUD endpoints the model is ready for but does not yet expose, the
  reconciled active-scene contradiction, the session-participant roster deferral
  and the inline-authorization decision are recorded as explicit dated
  decisions in the register below
  (*Deliberately-absent capabilities and authorization model recorded as
  decisions (CORE-SPEC-003)*), so each is auditable as an intentional omission
  rather than a gap.

## Deliberately-absent capabilities and authorization model recorded as decisions (CORE-SPEC-003)

This is the **single deferral/decision register** for the Core capabilities that
exist in the model but are deliberately not exposed, the one known active-scene
spec contradiction, and the inline-authorization decision. It consolidates the
many "…is a later story" and "…deliberately not wired here" markers scattered
through the source (the scene/content-block/entity/template/visibility repository
registrations in `apps/api/Program.cs`, `apps/api/Recaps/Recap.cs`,
`apps/api/Exports/ExportJob.cs`, `apps/api/Exports/ExportManifestProjection.cs`,
`apps/api/Content/ContentBlockEndpoints.cs`,
`apps/api/Realtime/SessionEventTypes.cs`,
`apps/api/Realtime/SessionEventRecipientResolver.cs` and `docs/11_REALTIME_SYNC.md`)
into one place, **dated, with a named owner (Core-later vs vertical) and a
rationale**, so a reader can tell an **intentional omission from a gap** (the
CORE-SPEC-003 acceptance criterion). It mirrors how CORE-DOC-002 (billing scope)
and CORE-EVT-004 (the session-event catalog) formalized their decisions above.

This register is **documentation only**: it adds no route, table, event or
migration and changes no Core source — it only records decisions — so all eleven
spec-consistency checks and the boundary scan stay green. It pairs with
CORE-SPEC-001 (the semantic spec-consistency checks) and CORE-EVT-004 (the
session-event catalog contract).

### (a) Deliberately-absent read/CRUD endpoints — deferred (owner: Core-later)

A capability whose domain model, persistence, EF migration and (where relevant)
role-based projection are implemented, but which has **no HTTP endpoint**, is a
deliberate deferral, not drift. `csv/api_routes.csv` is the single source of
truth for the mounted `/api/v1` routes and the spec-consistency check binds it to
the implementation **both directions** (check 6), so "no row in
`csv/api_routes.csv`" is the authoritative statement that the route does not
exist yet — an endpoint cannot be silently present or silently missing. The
following are deferred by design (date recorded **2026-06-15**):

- **User-data export pipeline and retention-based export expiry** (the export
  read/download route now exists). The export job (CORE-AUD-002, `ExportJob.cs`)
  and the workspace export manifest with its role-based projection (CORE-AUD-003,
  `ExportManifestProjection.cs`) were modeled, persisted and migrated, and the
  worker drives queued jobs into manifests (CORE-JOB-002); the **export
  read/download route is now mounted** — `GET /api/v1/exports/{exportId}`
  (CORE-EXP-001, `ExportEndpoints.cs`, `csv/api_routes.csv`), authorized to the
  "Export workspace" roles {Owner, Admin, Host} (`ExportAccessPolicy`; a
  non-authoring role is 403, a foreign-tenant/unknown-export/non-member is
  hidden-404, an incomplete/failed export is 409, all fail-closed), with the
  completed export's artifact (its manifest) returned role-projected through the
  existing `ExportManifestProjection` and delivered as an authorized stream —
  never a public/static URL (threats T4/T8). What remains deferred is the
  **user-data (`ExportScope.UserData`) export pipeline** — there is no producer of
  a user-data manifest yet, so only workspace exports are retrievable — and a
  **retention-based export expiry** (a true `ExpiresAt` with an object-storage
  purge of the artifact), which lands with the data-retention sweeps
  (CORE-PRIV-003); until then the only states that gate the download are the
  export's own lifecycle status. Owner: **Core-later** (the user-data export and
  retention stories). CORE-E2E-003 still asserts the export's role-based
  projection at the projection layer (the worker composition test exercises the
  worker, not the endpoint).
- **Separate participant reveal of a recap body** (the recap READ route now
  exists). The `Recap` aggregate, its persistence, EF migration and
  host-vs-audience role-based projection (`Recap.cs`) were implemented ahead of
  any route; the **recap read route is now mounted** —
  `GET /api/v1/sessions/{sessionId}/recap` (CORE-RCP-003, `RecapEndpoints.cs`,
  `csv/api_routes.csv`), authorized like the session read surface (any workspace
  member; foreign-tenant/unknown-session/non-member hidden-404, fail-closed) and
  role-projected through the existing `RecapProjection` so the audience receives
  the host-only-field-stripped summary. What remains deferred is the **separate
  participant reveal of a recap body**: a generated recap is host content
  ("Participant-visible only after separate reveal",
  `docs/09_EVENT_CATALOG.md` / `RecapGenerated`, threat **T2**), so a participant
  reading the recap read route never receives the body until that reveal lands.
  Owner: **Core-later** (the recap-reveal story). Rationale: the host-only body is
  guarded by the projection, not by the absence of a reveal route.
- **Entity-relationship-list / template / visibility-rule create
  and list endpoints.** The template and visibility-rule
  repositories (CORE-ENT-004, CORE-VIS-001) are implemented with **no
  list-everything method** and **no HTTP route** (`csv/api_routes.csv` defines
  none). Owner: **Core-later** (the respective endpoint stories — e.g.
  CORE-VIS-004). Rationale: the **explicit-ids contract** — the same-workspace
  coupling of `entity → entity_type`, an entity_relationship's two endpoints,
  `content_block → scene` and `visibility_rule → resource` is the create
  application flow's responsibility, **not** a database foreign key — is recorded
  on each aggregate; mounting a bare list/create route that does not resolve that
  coupling would invite a list-everything bypass of the tenant/workspace scoping
  (threat **T5**). **Resolved for generic entities (2026-06-16, CORE-ENT-006):**
  the entity create/list/by-id-read routes are now **mounted** under
  `/api/v1/workspaces/{workspaceId}/entities` — the create resolves the referenced
  `entity_type` through the **workspace-scoped** repository before inserting (so it
  honours, rather than bypasses, the same-workspace coupling above), and the
  list/read are **workspace-scoped** (no list-everything) and **projected by role**
  (an entity is content, so only the host-content roles receive its
  attribute-values; threats T2/T5). The entity **search** read with per-participant
  visibility filtering remains its own concern (CORE-ENT-005).
  **Resolved for entity types (2026-06-16, CORE-ENT-007):** the entity-TYPE
  create/list/by-id-read routes are now **mounted** under
  `/api/v1/workspaces/{workspaceId}/entity-types` — the type definition (template
  key plus field/type metadata) is stored as **data only** (no `if entityType`
  branching in Core source, docs/04), the list/read are **workspace-scoped** (no
  list-everything), and — because an entity type is an **authoring/schema artifact,
  not audience content** — all three routes are restricted to the **authoring
  roles** (`Owner`/`Admin`/`Host`/`CoHost`) with **no** host-vs-participant
  projection ("authorize like entity authoring"; a non-authoring member is `403`, a
  foreign/unknown type hidden-`404`, a duplicate per-workspace key or archived
  workspace `409`, all fail-closed; the create is audited as `EntityTypeCreated`).
- **Content-block list/get/update/revise route.** `ContentBlockEndpoints.cs`
  mounts only create and delete; there is deliberately **no
  list/get/update/revise route** (the revise capability lives on the aggregate
  for a later story). Owner: **Core-later**. Rationale: no such route is in
  `csv/api_routes.csv`.
- **Preview-as-participant HTTP endpoint.** The preview-as-participant query
  (CORE-VIS-003, `VisibilityPreviewService`) is implemented and **reused** by the
  participant-visible-feed projection (CORE-API-005) and the entity-search
  audience filtering (CORE-API-006), but it has **no dedicated preview HTTP
  route**. Owner: **Core-later** if a host-facing preview endpoint is ever
  required; a **vertical**'s host UI may instead consume the existing
  visible-feed projection. Rationale: visibility is decided in exactly one place
  (`docs/05_MODULE_CONTRACTS.md`: "do not duplicate visibility logic
  elsewhere"), so a second preview route is not needed for correctness and would
  duplicate the surface.

### (b) Decision: reveal-is-activation is the final scene-switch contract

**The one known spec contradiction.** The scene repository registration in
`apps/api/Program.cs` says a session "activates a scene through its active scene
pointer in a later story", while `apps/api/Realtime/SessionEventTypes.cs`
(`SceneActivated`) says "there is no separate active-scene command, so revealing
a scene to the audience **is** the scene switch". Read literally these disagree
on whether a separate active-scene command is coming.

**Decision (2026-06-15): reveal-is-activation is the final contract** for
switching the audience's active scene. There is **no separate active-scene
command and none is planned**: a host switches the audience's scene by
**revealing** it — the reveal command emits `SceneActivated`, gated through the
central Visibility engine so only the audience that may see the scene receives
the activation (CORE-EVT-003, threats T2/T3). The phrase "active scene pointer …
in a later story" is **not** a competing scene-switch mechanism; it survives only
as a narrow, optional future **session-state binding** whose sole purpose would
be to give the workspace-prepared `SceneCreated` / `ContentBlockCreated` events a
`session_id` to attach to (the **named owner** of those two **deferred** catalog
rows, CORE-EVT-004 above). Owner of that narrow binding: **Core-later** (a future
Sessions story); it is itself a deferral, not a gap — making
`session_events.session_id` optional instead would be an architecture change (an
ADR), out of scope. Either way the audience-facing scene switch **is**
reveal-is-activation; with this decision the spec is consistent on the point.

### (c) The session-participant roster deferral is a deliberate design, not a gap

`docs/11_REALTIME_SYNC.md` and
`apps/api/Realtime/SessionEventRecipientResolver.cs` note there is **no persisted
session-participant roster yet** (deferred to the Presence epic, CORE-PRS-001),
so the audience fan-out enumerates the **workspace's** active participants as the
candidate set. Recorded decision (2026-06-15): this is a **deliberate design**,
not a gap. Cross-session isolation correctness is held by **two** independent
mechanisms, not by a roster:

- **session-keyed realtime groups** — a connection joins only the groups of the
  session it connected to (`session:{sessionId}:hosts|observers|participant:{p}`),
  so a delivery addressed to `session:{thisSession}:participant:{p}` reaches a
  participant only when they are connected to **this** session; and
- the **session-scoped visibility gate** — every audience and per-recipient
  decision is delegated to the central Visibility engine **bounded by the event's
  session**, so it independently confirms the subject is revealed **in this
  session** (the cross-session leak, threats **T3/T5**).

Owner: **Core-later** (the Presence epic) for the roster as a presence
feature/optimization; it is **not required for correctness** today, so its
absence is intentional.

### (d) Decision: per-action authorization is inline by deliberate choice

Recorded decision (2026-06-15), from CORE-WS-005: per-action authorization is
performed **inline** at each endpoint, and a consolidated capability/policy
framework is **explicitly not pursued** — this is a **decision, not a gap**.
Rationale: the inline checks carry **security-relevant ordering** that a
capability-policy extraction would risk collapsing —

- **404-before-403** — existence is hidden before a role is consulted, so a
  low-role caller targeting a workspace they cannot see learns nothing about it
  (threats **T1/T5**);
- **membership-before-role** — object-level **workspace** membership is
  authorized before the **organization** role, so a non-member (even an
  organization Owner) gets a hidden `404`, not a `403`;
- **existence-before-state** — the resource is resolved within the already
  tenant-scoped lookup before its state is checked.

CORE-WS-005 pins these rules with a systematic authorization-policy **test
matrix** (`tests/LiveCore.Api.IntegrationTests/WorkspaceAuthorizationPolicyTests.cs`)
that asserts the exact `403`-vs-hidden-`404` distinction per route, end-to-end
over real HTTP. `MembershipRole` is **non-linear** (exact set-membership, never
an ordering comparison), so there is no policy lattice to factor out. Owner:
this decision is **final** (not deferred) — a reusable policy/handler framework
is intentionally not built; extracting one would trade the audited, per-route
ordering above for a uniform check that could silently reorder or merge the
status-code contract.

## Checking consistency

```bash
# Linux/macOS (PowerShell 7+)
pwsh -NoProfile -File scripts/spec-consistency.ps1
```

```powershell
# Windows (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/spec-consistency.ps1
```

The script exits `0` when every invariant holds, `1` when it finds drift (with a
per-finding report), and `2` on a configuration error (a spec file it cannot
find or parse). It runs eleven checks — the five name-set invariants
(CORE-DOC-001), five semantic invariants validated against the implementation
(CORE-SPEC-001), and the session-event catalog binding (CORE-EVT-004):

1. every route in the `docs/08` representative block is a row in
   `csv/api_routes.csv`;
2. the `docs/10` table list equals the table set in `csv/database_tables.csv`;
3. every non-deferred table in `csv/entitlement_database_tables.csv` exists in
   `csv/database_tables.csv`;
4. the `docs/09` event table equals the event set in `csv/event_catalog.csv`;
5. the `docs/18` epic list equals the union of the `epic` columns of
   `csv/core_epics_stories.csv` and `csv/core_phase2_epics_stories.csv`;
6. **routes vs implementation (both directions).** `csv/api_routes.csv` equals
   the `/api/v1` routes the minimal-API registrations (`apps/api/**/*Endpoints.cs`
   — `MapGroup` + `Map{Get,Post,Put,Delete}`) actually mount; a documented route
   that nothing mounts, or an undocumented endpoint, fails;
7. **route roles/auth.** Every `roles` cell in `csv/api_routes.csv` uses only real
   `MembershipRole` names (`apps/api/Organizations/MembershipRole.cs`) and a fixed
   set of audience descriptors, and the routes documented as unauthenticated
   provider callbacks (`roles = none (provider callback)`) are exactly the
   `AllowAnonymous` endpoints in code (both directions). The check does not (yet)
   re-derive each route's exact role *set* from the handler bodies, so a role
   *dropped/added* within the known vocabulary is not caught — only an unknown
   role, an empty cell, or an auth-posture flip;
8. **entitlement/store event catalog + AuditAction binding.**
   `csv/entitlement_event_catalog.csv` is well-formed and internally consistent:
   each event name is a unique generic PascalCase identifier, `persisted`/`audit`
   are booleans, and an event with no audit action (a blank/invalid `audit`) or one
   audited-but-not-persisted fails. CORE-SPEC-002 additionally **binds the catalog
   to the real `AuditAction` enum** (`apps/api/Audit/AuditAction.cs`): for every
   catalog event, `audit=true` **iff** a matching `AuditAction` member exists, so an
   `audit=true` event with no backing action — or an `AuditAction` whose catalog
   event is still `audit=false` — fails. The catalog is now a contract, not
   aspirational;
9. **mobile store CSV.** `csv/mobile_store_api_routes.csv` mirrors real `/api/v1`
   routes — each `/v1/…` path maps to a documented `/api/v1/…` route with the same
   method, `owner` is `Core`, and `auth_required` agrees with that route's
   documented authentication — and its path set equals the in-process
   `MobileApiGateway` route table (`apps/api/Hosting/MobileApiGateway.cs`);
10. **table columns/indexes.** `csv/database_tables.csv` equals the tables the EF
    Core model snapshot maps (`LiveCoreDbContextModelSnapshot.cs`, both
    directions), and the security/idempotency/tenant-isolation **unique** indexes
    the spec promises (idempotent purchases and store notifications, one buyer per
    receipt, per-subject entitlement/quota uniqueness, tenant/workspace slug
    uniqueness, the gap-free audit and session-event sequences) are declared
    `IsUnique()` in the snapshot.
11. **session-event catalog binding (CORE-EVT-004).** The emitted session-event
    set — the `public const string` members of
    `apps/api/Realtime/SessionEventTypes.cs` — equals the **non-deferred**
    `csv/event_catalog.csv` events (both directions): a non-deferred catalog event
    that no command emits, or a `SessionEventTypes` constant that is not a
    non-deferred catalog row, fails. A catalog row whose `notes` are marked
    `DEFERRED` is excluded from the comparison (today the workspace-prepared
    `SceneCreated`/`ContentBlockCreated`). The catalog is now a contract, not
    aspirational.

Checks 6–11 are the reason a spec change that touches a route, role, store event,
mobile path, table or session event must be reconciled with the code (or the code
with the spec) before CI goes green.
