# Specification Consistency

This note records the **single source of truth per concern** for the Core
specification, so the route, table, event and epic specs agree with each other
and with the implementation (CORE-DOC-001). When a documented item is not in the
implementation, it is listed here as **deferred** rather than left to drift.

The machine-checkable parts of this agreement are enforced by
`scripts/spec-consistency.ps1` (run locally or in CI alongside the boundary
scan).

## Sources of truth

| Concern | Single source of truth | Mirrors / derived views |
| --- | --- | --- |
| API routes | `csv/api_routes.csv` (mounted `/api/v1` routes) | `docs/08_API_CONTRACTS.md` representative list; `csv/mobile_store_api_routes.csv` (mobile-facing `/v1` path shape of the store/entitlement routes) |
| Database tables | `csv/database_tables.csv` (matches the EF Core model) | `docs/10_DATABASE_SCHEMA.md` table list; `csv/entitlement_database_tables.csv` (entitlement/store ownership view) |
| Session events | `csv/event_catalog.csv` | `docs/09_EVENT_CATALOG.md` table; `apps/api/Realtime/SessionEventTypes.cs` (the emitted subset) |
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

## Genuinely deferred items

These are documented for design intent but are **not** in the implemented
Core v1 schema/behavior. They are not drift; they are explicit deferrals or
in-scope-for-v1 items not yet built (noted per item).

- **`purchase_providers`** — provider handling is in-code (the purchase-provider
  abstraction, CORE-STORE-001), not a database table. Marked DEFERRED in
  `csv/entitlement_database_tables.csv`.
- **Planned-but-unemitted session events** — `SessionCreated`, `SceneCreated`,
  `ContentBlockCreated`, `PrivateMessageSent`, `AssetRevealed`,
  `SessionNoteCreated` and `RecapGenerated` are in the catalog but not yet
  emitted by any command. The emitted set is the eight names in
  `apps/api/Realtime/SessionEventTypes.cs`.

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
find or parse). It checks:

1. every route in the `docs/08` representative block is a row in
   `csv/api_routes.csv`;
2. the `docs/10` table list equals the table set in `csv/database_tables.csv`;
3. every non-deferred table in `csv/entitlement_database_tables.csv` exists in
   `csv/database_tables.csv`;
4. the `docs/09` event table equals the event set in `csv/event_catalog.csv`;
5. the `docs/18` epic list equals the union of the `epic` columns of
   `csv/core_epics_stories.csv` and `csv/core_phase2_epics_stories.csv`.
