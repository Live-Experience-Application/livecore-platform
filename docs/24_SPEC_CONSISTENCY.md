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
are unchanged. The remaining v1 acceptance bullets (refund/cancellation revocation
staying revoked) are CORE-MON-004.

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
