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

## Decision recorded (CORE-DOC-002): billing deferred for Core v1

`billing_account_links` was documented as a Store "Database addition"
(`docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`) but never built, so a
verified purchase has no way to link a store account to a Core subject and grant
that buyer a `SubjectEntitlement` — the purchase-to-entitlement chain is
incomplete. CORE-DOC-002 is the explicit decision on that gap.

**Decision: formally defer billing and the purchase-to-entitlement grant chain
to post-v1.** Billing is out of scope for Core v1
(`docs/01_PRODUCT_VISION_AND_SCOPE.md`, "Out of scope"), so completing the
monetization loop is deferred rather than built now. The following remain
**deferred** (documented design intent, not drift):

- the `billing_account_links` table (store-account-to-subject link) and the
  `purchase_providers` table (provider handling is in-code, CORE-STORE-001);
- the product → plan → entitlement mapping that turns a verified purchase into a
  plan grant;
- the trigger that would wire purchase verification (CORE-STORE-003/004) and
  store notifications (CORE-STORE-005, CORE-JOB-003) to
  `SubjectEntitlementAssignmentService.AssignFromPlanAsync`/`RevokeAsync`.

What Core v1 **does** ship of this area is intact and in scope (it is annotated
as such on the store/ad/entitlement rows of `csv/core_epics_stories.csv`):

- the provider-neutral verify-and-record gate — a verified purchase is persisted
  as an idempotent, auditable `purchase_transactions`/`purchase_events` record
  (CORE-STORE-001..004);
- the idempotent store-notification → purchase-status pipeline and its
  reconciliation job, which keep the persisted purchase status (the server-side
  source of truth) convergent (CORE-STORE-005, CORE-JOB-003);
- the reusable `SubjectEntitlement` assignment/lookup primitive and server-side
  quota enforcement (CORE-ENTL-001..004), and the entitlement-driven ad
  eligibility read (CORE-ADS-001), which operate on entitlements assigned by
  other means (administrative/seed assignment), not by a verified purchase.

**No verified purchase grants a `SubjectEntitlement` in Core v1.** This is a
scope decision consistent with `docs/01`, not an architecture change, so it is
recorded here rather than in an ADR (ADR 0010 already records Core-owned
entitlements/quotas and ADR 0011 that mobile ads stay outside Core). When
billing leaves deferral, the grant story adds `billing_account_links` + the
product→plan mapping and wires the existing verify/notify pipeline to the
existing assignment primitive, so no part of the v1 work is wasted.

## Genuinely deferred items

These are documented for design intent but are **not** in the implemented
Core v1 schema/behavior. They are not drift; they are explicit deferrals.

- **`purchase_providers`** — provider handling is in-code (the purchase-provider
  abstraction, CORE-STORE-001), not a database table. Marked DEFERRED in
  `csv/entitlement_database_tables.csv`.
- **`billing_account_links`** — the store-account-to-subject link table.
  Billing is out of scope for Core v1 (`docs/01_PRODUCT_VISION_AND_SCOPE.md`);
  **CORE-DOC-002 formally defers it** (and the rest of the
  purchase-to-entitlement grant chain) to post-v1 — see "Decision recorded
  (CORE-DOC-002)" above. Marked DEFERRED in
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
