# Entitlements, Quotas and Store Receipts

## Purpose

Mobile monetization requires the Core to enforce usage limits and premium capabilities server-side.

This module remains product-neutral. It must not contain DnD, campaign, player, DM, quest or store UI language.

## Core concepts

```text
PlanDefinition
EntitlementDefinition
SubjectEntitlement
QuotaDefinition
QuotaUsage
PurchaseProvider
PurchaseTransaction
PurchaseEvent
StoreNotificationEvent
BillingAccountLink
```

## Why this belongs in Core

Limits such as active workspace count, active session count, participant count, storage and ad-free state must be enforced server-side. Otherwise users can bypass mobile UI restrictions.

## What does not belong in Core

Core must not contain:

- ad placements
- App Store screenshots
- Google Play metadata
- DnD-specific pricing copy
- DM/player terminology
- native SDK code
- mobile UI paywalls

## Generic entitlement keys

Use generic keys:

```text
workspace.active.max
session.active.max
session.participant.max
asset.storage.bytes.max
ads.required
ads.disabled
hosted.sessions.ads.disabled
mobile.native.access
offline.cache.enabled
export.enabled
support.priority
```

ArcanOS may display these as:

```text
1 campaign
1 live session
4 players
ad-free player experience
unlimited campaigns
```

## Recommended ArcanOS mapping

Free host:

```text
workspace.active.max = 1
session.active.max = 1
session.participant.max = 4
asset.storage.bytes.max = 250MB
ads.required = true
```

Player Free:

```text
ads.required = true
mobile.native.access = true
offline.cache.enabled = limited
```

Player Plus:

```text
ads.disabled = true
mobile.native.access = true
offline.cache.enabled = true
```

Host Pro / Table Pro:

```text
workspace.active.max = fair_use_unlimited
session.active.max = fair_use_unlimited
session.participant.max = fair_use_unlimited
asset.storage.bytes.max = plan_defined
ads.disabled = true for host
hosted.sessions.ads.disabled = true if table pass included
export.enabled = true
```

## Receipt verification

The client never becomes the source of truth for premium access.

Flow:

```text
1. Mobile app completes store purchase.
2. Mobile app sends transaction token/JWS/purchase token to backend.
3. Backend verifies with Apple/Google server APIs.
4. Backend persists PurchaseTransaction.
5. Backend grants SubjectEntitlement.
6. Backend enforces quotas on every protected command.
7. Store server notifications update entitlement state on renewals, cancellations, refunds and grace periods.
```

## API surface

Generic routes:

```text
GET  /v1/me/entitlements
GET  /v1/me/quota-status
GET  /v1/workspaces/{workspaceId}/quota-status
POST /v1/purchases/apple/transactions
POST /v1/purchases/google/tokens
POST /v1/store-notifications/apple
POST /v1/store-notifications/google/rtdn
```

Apple/Google names are allowed here as infrastructure provider names, not product vertical names.

## Purchase provider abstraction (CORE-STORE-001)

The Store module isolates provider-specific verification behind a single port so that **Apple/Google provider
logic is isolated from Core domain logic** (the `Store Purchase Verification` epic's first acceptance criterion).

- `IPurchaseVerificationProvider` (`apps/api/Store/`) is the port: one adapter serves one `PurchaseProvider`
  (`Apple`/`Google`) and verifies an opaque proof against that store's server APIs.
- The abstraction is provider-neutral at both ends. Input is a `PurchaseVerificationRequest` (the provider plus
  the opaque proof — a transaction token / JWS / purchase token — and an optional opaque product reference);
  output is a `PurchaseVerificationResult` that is either `Verified`, carrying a normalized `VerifiedPurchase`
  (provider + provider transaction id + product reference), or `Rejected`, carrying a generic, log-safe reason and
  no purchase. Core never parses, trusts or logs the proof (`PurchaseVerificationRequest.ToString` excludes it).
- `PurchaseVerificationProviderResolver` selects an adapter by the generic `PurchaseProvider`. It is **fail-closed**:
  Core registers no adapter, so verification throws `PurchaseProviderNotConfiguredException` for every provider
  until a deployment wires one — the verification analogue of the fail-closed `UnconfiguredAssetStorage`
  (CORE-AST-002). The concrete, credential-bearing adapters (store SDK + keys) are deployment-supplied
  (`docs/13_SELF_HOSTING_REQUIREMENTS.md`); Core carries no native store SDK dependency and no store credentials.
- Authorization is upstream of the port: the later Apple (CORE-STORE-003) and Google (CORE-STORE-004) verification
  endpoints authorize the caller server-side, then resolve the adapter and verify. CORE-STORE-001 is the
  abstraction only — no store route, table or migration; persistence of the verified transaction is CORE-STORE-002.

## Purchase transaction persistence and audit trail (CORE-STORE-002)

CORE-STORE-001 produced a verified, provider-neutral `VerifiedPurchase` and deferred "persistence of the verified
transaction" to here. CORE-STORE-002 persists it and records its lifecycle as an audit trail, so **purchase state
changes are persisted and auditable** (the story's acceptance criterion; the "All purchase state changes must be
auditable" security requirement below). It adds two of the module's "Database additions" — `purchase_transactions`
and `purchase_events` (`apps/api/Store/`) — and the recording service over them, but **no** store HTTP route (the
verification endpoints are CORE-STORE-003/004).

- `PurchaseTransaction` is the persisted record of one verified purchase: the `provider`, the provider-assigned
  `provider_transaction_id`, the `product_reference`, the current lifecycle `status` and the record/update
  timestamps. It is created **only** from a `VerifiedPurchase` a provider adapter already verified server-side, so
  Core never trusts a client flag ("Never trust client-side premium flags"; "Never unlock limits before server
  verification succeeds").
- **Idempotent.** A purchase is named idempotently by the (`provider`, `provider_transaction_id`) pair — a
  provider transaction id is unique within its provider — so the unique
  `purchase_transactions(provider, provider_transaction_id)` index makes recording the same verified purchase
  twice (a client retry, a replayed proof, a duplicate notification) a safe no-op that creates no second row and
  no duplicate audit event ("Store notifications must be idempotent"). This is the persistence analogue of the
  unique `idempotency_keys(scope, key)` index.
- **Auditable.** `PurchaseEvent` is the append-only `purchase_events` trail: each row records one state change as
  a `previous_status` (NULL for the initial recording) → `new_status` pair, so a purchase's lifecycle is fully
  reconstructable from immutable facts (mirrors the append-only `audit_logs` and `session_events`). The
  `purchase_transaction_id` foreign key cascades — the trail is part of the transaction's own history.
- **Status lifecycle.** A verified purchase is recorded `Active`; the generic states `Active`/`Cancelled`/`Refunded`/
  `InGracePeriod` model renewals, cancellations, refunds and grace periods (grace periods represented **explicitly**
  per the security requirements). `PurchaseTransactionService` records a verified purchase and audits each status
  change; a no-op change (to the status the transaction is already in) writes no event. **Which** provider
  notification drives **which** transition, the idempotent ingestion of those notifications, and the entitlement
  downgrade/revocation a refund or cancellation causes are the later store-notification story (CORE-STORE-005); this
  story models only the persisted state and its auditable change.
- **Not tenant content, no buyer column.** `purchase_transactions` carries no `organization_id` and no buyer
  column: a purchase is named globally by its (`provider`, `provider_transaction_id`) pair, and the buyer/subject
  linkage is the separate `billing_account_links` table (a later story), exactly as this document lists the two as
  distinct "Database additions". The stored identifiers are not secrets — only the raw proof is, and the proof is
  **never persisted** (threat T7).
- Authorization is upstream: the verification endpoints (CORE-STORE-003/004) authorize the caller server-side and
  verify the proof **before** recording, and the store-notification handler (CORE-STORE-005) drives the status
  changes; this story supplies the generic, reusable persistence + audit primitives they build on.

## Apple transaction verification endpoint (CORE-STORE-003)

CORE-STORE-001 isolated provider verification behind the `IPurchaseVerificationProvider` port, and CORE-STORE-002
added the idempotent, auditable persistence of a verified purchase. CORE-STORE-003 wires them into the first
**store HTTP route** — the Apple side of "Mobile app sends transaction token/JWS/purchase token to backend;
Backend verifies with Apple/Google server APIs; Backend persists PurchaseTransaction" (the "Receipt verification"
flow above) — so that **Apple transaction data is verified before entitlements are granted** (the story's
acceptance criterion).

- `POST /api/v1/purchases/apple/transactions` (`apps/api/Store/ApplePurchaseEndpoints.cs`) — the route of
  `csv/mobile_store_api_routes.csv` (`POST /v1/purchases/apple/transactions`) surfaced under the Core `/api/v1`
  prefix `docs/08_API_CONTRACTS.md` mandates, and added to `csv/api_routes.csv`. The request body carries only the
  opaque Apple App Store **signed transaction (JWS) / transaction proof** and an optional opaque product
  reference; Core never parses, trusts or logs the proof (it is carried verbatim into a provider-neutral
  `PurchaseVerificationRequest`).
- **Verify-then-record, fail-closed.** The endpoint authorizes the caller, resolves the deployment-supplied Apple
  adapter through `PurchaseVerificationProviderResolver` and verifies the proof, and **only a verified result** is
  persisted as a `PurchaseTransaction` (reusing the CORE-STORE-002 `PurchaseTransactionService`, so recording is
  idempotent — a retry or a replayed-but-genuine proof creates no second row and no duplicate audit event). A
  rejected (forged / replayed / unverifiable) proof is `422` and records **nothing**; when no Apple adapter is
  configured the resolver fails closed and the request is `503` (the verification analogue of the unconfigured
  asset storage). So Core never trusts a client's premium claim and never grants premium state without a real
  server-side verification behind it ("Never trust client-side premium flags"; "Never unlock limits before server
  verification succeeds").
- **Authorization.** A missing/invalid token is `401`. Submitting a purchase is an inherently per-user action (the
  buyer's own receipt), so a non-user **service-account** principal is denied `403` — it has no personal purchase
  to submit (the same rule as the `/me` quota-status read). The transaction is named **globally** by its
  (`provider`, `provider_transaction_id`) pair and carries **no tenant** (CORE-STORE-002: `purchase_transactions`
  has no `organization_id`), so there is no organization/workspace boundary to resolve on this route. The request
  body is validated only **after** authorization, so an unauthorized caller never receives request-shape feedback.
- **Out of scope (later stories).** Granting the resulting `SubjectEntitlement` from the recorded purchase (the
  product → plan → entitlement mapping) and linking the buyer (`billing_account_links`) are later stories;
  CORE-STORE-003 establishes the verify-and-record gate they sit behind. The Google purchase-token endpoint is
  CORE-STORE-004, and the idempotent store-notification handling that drives renewals / cancellations / refunds /
  grace periods is CORE-STORE-005. This story adds no new table and no EF migration (it reuses the CORE-STORE-002
  `purchase_transactions` and `purchase_events`).

## Google purchase token verification endpoint (CORE-STORE-004)

CORE-STORE-003 wired the Apple side of the receipt-verification flow; CORE-STORE-004 adds the **Google** side —
the Google half of "Mobile app sends transaction token/JWS/purchase token to backend; Backend verifies with
Apple/Google server APIs; Backend persists PurchaseTransaction" (the "Receipt verification" flow above) — so that
**Google purchase tokens are verified before entitlements are granted** (the story's acceptance criterion). It is
the Google analogue of CORE-STORE-003: the same verify-then-record, fail-closed shape over the same CORE-STORE-001
abstraction and CORE-STORE-002 persistence, differing only in the provider and the proof's name.

- `POST /api/v1/purchases/google/tokens` (`apps/api/Store/GooglePurchaseEndpoints.cs`) — the route of
  `csv/mobile_store_api_routes.csv` (`POST /v1/purchases/google/tokens`) surfaced under the Core `/api/v1`
  prefix `docs/08_API_CONTRACTS.md` mandates, and added to `csv/api_routes.csv`. The request body carries only the
  opaque Google Play **purchase token** and an optional opaque product reference (a Google Play product/SKU); Core
  never parses, trusts or logs the token (it is carried verbatim into a provider-neutral
  `PurchaseVerificationRequest`).
- **Verify-then-record, fail-closed.** The endpoint authorizes the caller, resolves the deployment-supplied Google
  adapter through `PurchaseVerificationProviderResolver` and verifies the token, and **only a verified result** is
  persisted as a `PurchaseTransaction` (reusing the CORE-STORE-002 `PurchaseTransactionService`, so recording is
  idempotent — a retry or a replayed-but-genuine token creates no second row and no duplicate audit event). A
  rejected (forged / replayed / unverifiable) token is `422` and records **nothing**; when no Google adapter is
  configured the resolver fails closed and the request is `503` (the verification analogue of the unconfigured
  asset storage). So Core never trusts a client's premium claim and never grants premium state without a real
  server-side verification behind it ("Never trust client-side premium flags"; "Never unlock limits before server
  verification succeeds").
- **Authorization.** A missing/invalid token is `401`. Submitting a purchase is an inherently per-user action (the
  buyer's own receipt), so a non-user **service-account** principal is denied `403` — it has no personal purchase
  to submit (the same rule as the Apple endpoint and the `/me` quota-status read). The transaction is named
  **globally** by its (`provider`, `provider_transaction_id`) pair and carries **no tenant** (CORE-STORE-002:
  `purchase_transactions` has no `organization_id`), so there is no organization/workspace boundary to resolve on
  this route. The request body is validated only **after** authorization, so an unauthorized caller never receives
  request-shape feedback.
- **Out of scope (later stories).** Granting the resulting `SubjectEntitlement` from the recorded purchase (the
  product → plan → entitlement mapping) and linking the buyer (`billing_account_links`) are later stories;
  CORE-STORE-004 establishes the verify-and-record gate they sit behind. The idempotent store-notification handling
  that drives renewals / cancellations / refunds / grace periods is CORE-STORE-005. This story adds no new table and
  no EF migration (it reuses the CORE-STORE-002 `purchase_transactions` and `purchase_events`).

## Idempotent store notification handling (CORE-STORE-005)

CORE-STORE-002 modeled the persisted purchase and its append-only `purchase_events` audit trail and deferred "the
idempotent ingestion of those notifications, and the entitlement downgrade/revocation a refund or cancellation
causes" to here. CORE-STORE-005 is that ingestion: it implements the **store-notification handler** that realizes
step 7 of the "Receipt verification" flow above — "Store server notifications update entitlement state on
renewals, cancellations, refunds and grace periods" — so that **renewals, cancellations, refunds and grace
periods update entitlements safely** (the story's acceptance criterion). It adds the module's last documented
"Database addition" — `store_notification_events` (`apps/api/Store/`) — and the two store-notification HTTP routes.

- `POST /api/v1/store-notifications/apple` (Apple App Store Server Notifications) and
  `POST /api/v1/store-notifications/google/rtdn` (Google Real-time Developer Notifications via Pub/Sub push) —
  the routes of `csv/mobile_store_api_routes.csv`, surfaced under the Core `/api/v1` prefix `docs/08_API_CONTRACTS.md`
  mandates and added to `csv/api_routes.csv`. They live in `apps/api/Store/StoreNotificationEndpoints.cs`.
- **Unauthenticated at the HTTP layer, authentic via signature/source.** A store delivers these notifications
  server-to-server, so the routes carry no OIDC bearer token (`csv/mobile_store_api_routes.csv`:
  `auth_required=false`) and are mapped `AllowAnonymous`. The **only** thing that makes an inbound payload
  trustworthy is the deployment-supplied `IStoreNotificationParser` adapter validating its **signature/source**
  ("Must validate signature/idempotency" / "Must validate source/idempotency"). This is the notification analogue
  of the CORE-STORE-001 `IPurchaseVerificationProvider` port: one adapter per provider, validates and reduces the
  opaque raw payload to a provider-neutral `StoreNotification` (provider + the store's unique notification id +
  the actionable type + the affected purchase's provider transaction id), so **provider logic is isolated from
  Core domain logic**. The concrete, credential-bearing validators are deployment-supplied
  (`docs/13_SELF_HOSTING_REQUIREMENTS.md`; threat T7); Core carries no store SDK and no signing keys. Until one is
  wired the `StoreNotificationParserResolver` **fails closed** (`StoreNotificationParserNotConfiguredException`),
  so an inbound notification is `503` and **never changes a purchase without a real validator behind it** — the
  notification analogue of the unconfigured asset storage and the unconfigured purchase verifier. A payload the
  adapter rejects as forged/unparseable is `400` and records nothing; an authentic but non-actionable payload is
  acknowledged `200` and records nothing.
- **Idempotent (the headline requirement; "Store notifications must be idempotent").** Idempotency is two-layered:
  the **dedup ledger** — `store_notification_events`, keyed by the unique
  `store_notification_events(provider, provider_notification_id)` index (the store's own notification id is unique
  within its provider) — recognizes a re-delivered notification and ignores it with no second effect, exactly as
  the unique `purchase_transactions(provider, provider_transaction_id)` and `idempotency_keys(scope, key)` indexes
  work; and the **idempotent effect** — the purchase status change it drives **reuses**
  `PurchaseTransactionService.ChangeStatusAsync` (CORE-STORE-002), which writes no purchase event for a no-op
  transition — so even two notifications that race past the dedup apply at most one real change and one audit event.
- **Safe entitlement update / downgrade path.** The notification's actionable type maps to exactly one target
  purchase status: a **renewal** keeps/reactivates `Active`, a **cancellation** downgrades to `Cancelled`, a
  **refund/chargeback** revokes to `Refunded`, and a **grace period** moves to the explicit `InGracePeriod` state
  ("Refunds and chargebacks must revoke or downgrade entitlements"; "Grace periods must be represented
  explicitly"). The persisted purchase status is the **server-side source of truth** for premium state ("User-visible
  premium state must come from server entitlements"), so updating it **is** the safe entitlement update. The change
  is audited twice over: the purchase-side fact is the `purchase_events` trail (CORE-STORE-002) and the
  notification-side fact is the append-only `store_notification_events` row (the event catalog's
  `StoreNotificationProcessed`). A notification for a purchase Core never recorded is `TransactionNotFound` —
  nothing is fabricated (fail-closed) — but its arrival is still recorded so it is auditable and not reprocessed.
- **Out of scope (a later story).** Granting/revoking the linked `SubjectEntitlement` from a purchase requires the
  buyer linkage (the separate `billing_account_links` "Database addition", which `purchase_transactions` deliberately
  has no column for) plus the product → plan → entitlement mapping; both are deferred. CORE-STORE-005 delivers the
  idempotent notification → purchase-status pipeline (the server-side source of truth) that a future grant/revoke
  story consumes as its trigger. The `store_notification_events` row stores only the **normalized** identifiers,
  never the raw notification body (which may embed signed receipt content — threat T7).

## Store notification reconciliation job (CORE-JOB-003)

CORE-STORE-005 processes store notifications **only on the synchronous inbound webhook**, in delivery order. But a
store delivers at least once and can **reorder or drop** deliveries, so a purchase's persisted status can drift
from the status the latest event implies: an older notification applied after a newer one (out-of-order), or a
notification that arrived before the purchase was ever recorded and so applied nothing (missed,
`TransactionNotFound`). CORE-JOB-003 adds the **reconciliation job** that re-derives entitlement state from the
ledger so it converges — "Missed or out-of-order store notifications are reconciled so entitlement state
converges; idempotent; only runs when billing is configured" (the story's acceptance criterion). It is the
worker's fourth periodic job (`apps/worker`; `docs/02_ARCHITECTURE.md`: the worker owns async jobs), behind no
HTTP route.

- **Re-derives from `store_notification_events` and `purchase_events`.** `store_notification_events` is extended
  with the store's reported `occurred_at` event time (a new column on the CORE-STORE-005 ledger, distinct from the
  existing `received_at` delivery time, because the delivery time never reflects the store's true event ordering).
  The converged status is the `applied_status` of the notification with the **latest `occurred_at`** for a purchase
  — regardless of the order the notifications were delivered or applied — and the purchase's current status is the
  head of the append-only `purchase_events` trail. A non-unique index on
  `store_notification_events(provider, provider_transaction_id, occurred_at)` serves the re-derivation lookup.
- **Reuses `StoreNotificationService`** (the story note). `StoreNotificationService.ReconcileTransactionAsync`
  re-derives a purchase's converged status and drives it there by **reusing** the same audited, idempotent
  `PurchaseTransactionService.ChangeStatusAsync` the webhook uses (CORE-STORE-002), stamped with the authoritative
  notification's event time — so the convergence is audited on the `purchase_events` trail exactly as a
  webhook-driven change is, and no parallel reconciliation pipeline is built. The
  `StoreNotificationReconciliationService` sweep (in the Store module) picks drifted purchases from
  `IReconcilablePurchaseReader` and converges each; a reconciled purchase matches its latest notification and drops
  out, so a bounded sweep makes progress.
- **Idempotent and fail-closed.** Reconciliation re-derives from immutable ledger facts and converges to the same
  state every time (a consistent purchase is a no-op — no status change, no audit event), so a re-run or a
  crash-retried sweep changes nothing. A notification recorded for a purchase Core never persisted converges
  nothing (`TransactionNotFound`): nothing is fabricated, so no entitlement is granted without a real verified
  purchase. Purchases are global (no tenant/buyer column), so there is no tenant boundary on this system job.
- **Gated on billing, fail-closed (`only runs when billing is configured`).** Billing/monetization is in scope for
  Core v1 (`docs/01_PRODUCT_VISION_AND_SCOPE.md`), but it requires deployment-supplied store adapters and credentials
  (`docs/13_SELF_HOSTING_REQUIREMENTS.md`), so the job runs only when a deployment has both a
  configured database **and** `Store:Reconciliation:Enabled=true`. With the flag unset (the default) the worker
  registers no reconciliation loop — the same fail-closed posture as the verification/notification parser
  resolvers, which register no adapter until a deployment supplies one. The worker schedules it every
  `Store:Reconciliation:SweepInterval` in bounded `Store:Reconciliation:BatchSize` batches.
- **Out of scope (later stories).** Granting/revoking the linked `SubjectEntitlement` from the converged purchase
  status (which needs the buyer linkage, the separate `billing_account_links` table) is deferred, as is a SQL
  window-function candidate query for high-volume deployments (the candidate scan computes the latest-per-purchase
  client-side, which suits this off-by-default, low-volume job).

## Purchase-to-entitlement grant chain — in scope for Core v1 (CORE-MON-001)

Every store story above stops at the **persisted, audited purchase status** and
defers "granting the resulting `SubjectEntitlement` from the recorded purchase
(the product → plan → entitlement mapping) and linking the buyer
(`billing_account_links`)" to "a later story". CORE-DOC-002 had recorded that
chain as **deferred to post-v1**; CORE-MON-001 **reverses that decision** — the
product now requires monetization in v1, so the grant chain is **in scope for
Core v1** and is built by the Monetization v1 epic (CORE-MON-001..010). The
single source of truth for the v1 monetization scope and acceptance is
`docs/24_SPEC_CONSISTENCY.md` ("Decision recorded (CORE-MON-001)").

The chain to build in v1 (the later stories the store sections point to):

- the `billing_account_links` "Database addition" (store-account-to-subject
  link) — `purchase_transactions` deliberately carries no buyer column, so the
  buyer linkage lives here (CORE-MON-002);
- the product → plan → entitlement mapping that turns a verified purchase into a
  plan grant (CORE-MON-003);
- the trigger that calls `SubjectEntitlementAssignmentService` from a verified
  purchase (CORE-STORE-003/004) or a store notification (CORE-STORE-005,
  CORE-JOB-003), including the refund/cancellation revocation path
  (CORE-MON-003/004).

The v1 monetization foundation already shipped is reused, not rebuilt: the
provider-neutral verify-and-record gate (CORE-STORE-001..004), the idempotent
store-notification → purchase-status pipeline and its reconciliation job
(CORE-STORE-005, CORE-JOB-003), the reusable `SubjectEntitlement`
assignment/lookup primitive and server-side quota enforcement
(CORE-ENTL-001..004), and the entitlement-driven ad eligibility read
(CORE-ADS-001). The grant story wires this existing verify/notify pipeline to the
existing assignment primitive over the new `billing_account_links` + product→plan
mapping — no part of the foundation work is wasted.

**v1 acceptance** (recorded in full in `docs/24_SPEC_CONSISTENCY.md`): a
verified, buyer-linked purchase grants the buyer the mapped `SubjectEntitlement`
idempotently and the grant shows up in the effective-entitlements read; a
refund/cancellation/chargeback revokes or downgrades it and stays revoked; the
free-tier quotas are enforced server-side and cannot be bypassed by clients; and
user-visible premium state comes only from server entitlements (an
unverified/failed purchase grants nothing — fail-closed).

## Purchase-to-entitlement grant chain — implemented (CORE-MON-003)

CORE-MON-001 declared the grant chain in scope for v1 and CORE-MON-002 landed the
buyer linkage; CORE-MON-003 implements the **grant** itself — step 5 of the
"Receipt verification" flow above ("Backend grants `SubjectEntitlement`") — so a
verified, buyer-linked purchase now grants the buyer the mapped
`SubjectEntitlement` (the v1 monetization acceptance in `docs/24_SPEC_CONSISTENCY.md`).

- **It wires, it does not duplicate.** The plan → entitlement bundle already
  exists (`PlanDefinition.Entitlements`, CORE-ENTL-001) and the server-side
  assignment already exists (`SubjectEntitlementAssignmentService.AssignFromPlanAsync`,
  CORE-ENTL-002). The new `ProductEntitlementGrantService` (`apps/api/Entitlements/`)
  supplies only the remaining **product → plan** step and reuses both. **No new
  table is introduced** — the chain composes the existing
  `plan_definitions`/`plan_entitlements`/`subject_entitlements` model, so the
  documented "Database additions" list is unchanged.
- **The product → plan mapping is by plan key.** A verified purchase's
  `product_reference` (the vertical's opaque store product identifier) is mapped to
  a generic plan by the plan's stable `PlanDefinition.Key` (reusing
  `IPlanDefinitionRepository.FindByKeyAsync`). Core provides only the generic
  mechanism; the vertical supplies the plan-definition **seed data** whose keys
  correspond to the store products it sells (the concrete commercial plans are
  vertical seed data, never hardcoded in Core — see `PlanDefinition` above and
  `docs/04_PRODUCT_BOUNDARIES.md`).
- **Idempotent on (purchase, entitlement).** A verified purchase maps to exactly
  one subject (the unique `billing_account_links(purchase_transaction_id)` link,
  CORE-MON-002) and deterministically to one plan (by product reference), and the
  assignment is idempotent per (subject, entitlement) (the unique per-subject index,
  upsert-in-place — CORE-ENTL-002). So a duplicate webhook / retry / replayed-but-
  genuine proof converges rather than double-granting, and the grant shows up in the
  effective-entitlements read (`GET /api/v1/me/entitlements`).
- **One transaction, fail-closed.** The Apple (CORE-STORE-003) and Google
  (CORE-STORE-004) verification endpoints perform the record + buyer link + grant in
  **one** transaction (reusing the CORE-CONC-002 unit of work). The grant runs only
  when the purchase belongs to the resolved buyer (a fresh link or this buyer's
  idempotent re-link): a conflicting cross-subject claim grants nothing (the 409
  path), an unverified/failed purchase never reaches the grant (the 422 path), and a
  product reference that maps to no active plan grants nothing.
- **Audit.** The grant produces the catalog's `EntitlementGranted` domain event
  (`csv/entitlement_event_catalog.csv`); backing the entitlement/store event catalog
  with a dedicated `AuditAction` is CORE-SPEC-002. The refund/cancellation
  **revocation** side of the chain, and the monotonic (absorbing-revoked) purchase
  state machine, are CORE-MON-004.

## Monotonic purchase status and refund revocation — implemented (CORE-MON-004)

CORE-MON-003 implemented the **grant** side of the chain and pointed the
refund/cancellation **revocation** side, plus the monotonic purchase state machine,
to here. CORE-MON-004 implements them — the second v1 monetization acceptance
bullet (`docs/24_SPEC_CONSISTENCY.md`): a refund/cancellation/chargeback revokes the
granted entitlement and **stays revoked** ("Refunds and chargebacks must revoke or
downgrade entitlements", the Security requirements below; "a revoked state is
terminal").

- **Monotonic, absorbing revoked states.** The purchase status machine
  (`PurchaseTransaction.ChangeStatus`, backed by the product-neutral
  `PurchaseTransactionStatusMachine`) is now **monotonic**: the revoked states
  `Refunded` (which a refund/chargeback drives) and `Cancelled` are **terminal /
  absorbing**. Once a purchase is in one, no later notification — a renewal back to
  `Active`, a grace period, or even the other revoked kind — can move it. Previously
  `ChangeStatus` allowed **any** transition, so a legitimate `DID_RENEW` with a later
  event time than a refund flipped `Refunded → Active` and silently re-granted
  premium; the absorbing rule closes that. The non-revoked states (`Active`,
  `InGracePeriod`) still transition freely, so a grace-period → renewal recovery is
  unaffected. A forbidden move is a no-op (no state change, no audit event), so a
  late but legitimate notification is simply ignored.
- **Reconciliation cannot resurrect a refund.** The reconciliation re-derivation
  (`StoreNotificationService.ReconcileTransactionAsync` and the
  `ReconcilablePurchaseReader` candidate scan) now computes a purchase's converged
  status as a **monotonic fold** over *all* its recorded notifications in event-time
  order (`PurchaseTransactionStatusMachine.Converge`), not the single latest-by-event-
  time notification. So a refund stays revoked even when a later renewal was recorded
  after it, and a purchase already in a revoked state is never a reconciliation
  candidate (it can neither drift nor be reconciled away).
- **On entering a revoked state, the granted entitlement is revoked.** When a
  notification (or its reconciliation) drives a purchase into a revoked state, the
  granted `SubjectEntitlement` is revoked through
  `PurchaseEntitlementRevocationService` — the **inverse of the CORE-MON-003 grant
  chain**: it resolves the buyer from the `billing_account_links` link and revokes
  the entitlements the purchase's product maps to (reusing
  `ProductEntitlementGrantService.RevokeForProductAsync` over the existing
  `SubjectEntitlementAssignmentService.RevokeAsync`). The revoke runs **before** the
  status change is committed, so a revoke failure leaves the work unfinished and the
  store's re-delivery (or the next reconciliation sweep) retries it; it is idempotent
  (revoking an already-revoked or never-held entitlement is a safe no-op), and
  fail-closed (an unrecorded/unlinked purchase or an unmapped product revokes
  nothing). The revoked premium disappears from the effective-entitlements read
  (`GET /api/v1/me/entitlements`) and stays gone.
- **Hardened event time.** `StoreNotification.OccurredAt` — the authoritative
  ordering key reconciliation derives the converged status from — is validated: a
  defaulted/absurdly-early time is rejected by `StoreNotification.Create`, and an
  implausibly-far-future time is rejected at the ingestion endpoint against the
  host clock (a 24h skew tolerance). This is defence in depth on the ordering key;
  the monotonic machine already prevents a manipulated time from resurrecting a
  refund.
- **No new table.** The chain composes the existing
  `purchase_transactions`/`purchase_events`, `billing_account_links` and
  `subject_entitlements` model; no schema change and no EF migration. With
  CORE-MON-004 the v1 monetization loop (grant on verified purchase, revoke and
  stay revoked on refund) is complete.

## Atomic quota check-and-consume (CORE-CONC-004)

Server-side quota enforcement (CORE-ENTL-004) is the gate that makes "Free limits cannot be bypassed
by clients" real, so the check-and-consume it performs must be safe under concurrency. CORE-CONC-004
makes it atomic: `QuotaEnforcementService.TryConsumeAsync` performs the limit check **and** the usage
increment as a SINGLE limit-guarded statement
(`UPDATE quota_usage SET used_amount = used_amount + @amount WHERE … AND used_amount + @amount <= @limit`),
not a separate read-then-write. The database re-evaluates the cap against the row it locks, so two
concurrent protected commands can never both pass the limit — N parallel `session/start` /
`workspace/create` at a limit of one yield exactly one success and N-1 quota-exceeded, and
`session.active.max` / `workspace.active.max` can never be exceeded under a race.

It stays fail-closed and reuses the existing `quota_usage` table (no schema change): a subject not
entitled to a defined quota has no allowance and consumes nothing; an unlimited (fair-use) grant
increments unconditionally; an ungoverned command (no active quota definition) consumes nothing. A
command that frees a counted resource (a session ending) releases the unit with a clamped decrement,
so an "active" quota reflects the current count.

## Server-side participant cap on the join path (CORE-MON-005)

CORE-ENTL-004 enforced `workspace.active.max` (on workspace create) and
`session.active.max` (on session start) but left `session.participant.max` — the
headline free-tier participant cap (`csv/mobile_entitlement_catalog.csv` scope
`session`, free value 4) — enforced **nowhere**: the participant-join decision
(`SessionParticipantJoinService.JoinAsync`) performed no quota check, so the cap was
UI-only and trivially bypassed by a client that simply did not render the paywall —
the exact failure this document says Core exists to prevent ("Limits ... must be
enforced server-side. Otherwise users can bypass mobile UI restrictions"). CORE-MON-005
closes that: the participant cap is now enforced server-side on the join path, so a
participant join is **rejected once the session is at its plan participant limit** (the
story's acceptance criterion).

- **A new quota subject — the session.** `session.participant.max` counts the
  participants admitted to **one** session, so its usage must be measured per session,
  not per workspace (a workspace runs many sessions, each with its own cap). The
  `EntitlementSubjectType` therefore gains a third kind, **`Session`**, alongside
  `User` and `Workspace`; the quota is keyed by (`Session`, sessionId), so each session
  has an independent participant counter. The enum is persisted by its stable name, so
  the new value needs **no schema change and no EF migration** (the
  `subject_entitlements`/`quota_usage`/`quota_definitions` `subject_type` columns already
  store the name). The generic key is unchanged — `session.participant.max` was already
  in the "Generic entitlement keys" list and the catalog — so the catalog and that list
  are not edited.
- **Enforced via the existing quota services, atomically (CORE-CONC-004).** The join
  service's **last** gate — after every existence / tenant-isolation / lifecycle check —
  atomically check-and-consumes one unit of the session's participant quota through the
  reused `QuotaEnforcementService.TryConsumeAsync`. Because that is a single
  limit-guarded statement, **concurrent joins can never overrun the cap**: N joins
  racing for the last slot yield exactly one admission and N-1 quota-exceeded (the
  story's concurrency acceptance). A join denied for any earlier reason consumes
  nothing, and a denied consume emits no `ParticipantJoined` event — an over-cap join is
  never recorded or delivered (fail-closed, exactly like every other join denial).
- **Fail-closed, and only enforces a quota that exists.** A free session admits up to
  its small granted cap; a paid plan's larger or unlimited (fair-use) grant admits more
  — premium state comes only from the server entitlement. When **no**
  `session.participant.max` quota governs the deployment the join is ungoverned and
  proceeds (Core enforces only quotas that exist); when the quota **is** defined but the
  session holds no grant, the session has no allowance and the join is denied — the same
  fail-closed contract as `session.active.max`. A deployment that wants the free cap
  defines the quota and grants every session the free entitlement (the grant-chain /
  presence wiring, CORE-MON / CORE-PRS).
- **Symmetric release on leave.** A leaving participant
  (`SessionParticipantLeaveService.LeaveAsync`) releases the consumed
  `session.participant.max` slot, so the counter reflects the session's **current**
  participants rather than a lifetime total — exactly as `session.active.max` is consumed
  at start and released at end. The release is a clamped, idempotent decrement (a no-op
  when nothing was recorded), so it is safe on the idempotent-no-op / not-found leave
  paths and in an ungoverned deployment. **No new table and no EF migration** — the chain
  reuses the existing `quota_usage` table.
- **Where the join path runs.** `JoinAsync`/`LeaveAsync` are the reusable decisions; the
  production caller that wires a real join/leave entry point so the cap applies end-to-end
  is CORE-PRS-001. The check and its tests are added here against the service, per the
  story note.

## Server-side storage cap on the upload path (CORE-MON-006)

CORE-ENTL-004 enforced `workspace.active.max` and `session.active.max`, and CORE-MON-005 added
`session.participant.max`, but `asset.storage.bytes.max` — the free-tier storage cap
(`csv/mobile_entitlement_catalog.csv`, a `Bytes` quota) — was enforced **nowhere**: no key
existed in `QuotaEntitlementKeys`, and the upload-intent command
(`AssetUploadIntentService.CreateAsync` / `AssetEndpoints.CreateUploadIntentAsync`) performed no
quota check, so a free workspace had **unbounded** storage — the exact bypass this document says
Core exists to prevent ("Limits such as ... storage ... must be enforced server-side. Otherwise
users can bypass mobile UI restrictions"). CORE-MON-006 closes that: an asset upload is **rejected
when the workspace would exceed its plan storage quota**, enforced server-side at upload-intent,
and freeing an asset restores headroom (the story's acceptance criterion).

- **The workspace is the storage-quota subject.** Asset bytes accumulate per **workspace** (an
  asset is workspace-scoped, `csv/database_tables.csv`), so `asset.storage.bytes.max` is keyed by
  (`Workspace`, workspaceId) — the same subject the workspace quota-status route already surfaces
  (CORE-ENTL-003), exactly like `session.active.max`. No new `EntitlementSubjectType` is needed
  (`Workspace` already exists), the generic key was already in the "Generic entitlement keys" list
  and the catalog, and the unit is the existing `QuotaUnit.Bytes` — so there is **no new table and
  no EF migration**, and the catalog and that key list are not edited.
- **Enforced via the existing quota services, atomically (CORE-CONC-004).** The client declares the
  object's size at upload-intent (a new `sizeBytes` request field); after every authorization /
  tenant / workspace / role check the command atomically check-and-consumes those bytes against the
  workspace's storage quota through the reused `QuotaEnforcementService.TryConsumeAsync`. Because
  that is a single limit-guarded statement, **concurrent uploads can never overrun the cap**: N
  uploads racing for the last bytes yield exactly the admissions that fit and the rest are
  quota-exceeded. The consume, the signed-URL mint and the asset-row persist run in **one**
  `TransactionalUnitOfWork`, so an over-quota upload (409) and a fail-closed storage error (503,
  `AssetStorageNotConfiguredException` thrown inside the transaction) both roll back having consumed
  **nothing** and persisted **nothing** — no orphan pending asset and no leaked quota.
- **Fail-closed, and only enforces a quota that exists.** A free workspace's small storage cap is
  enforced; a paid plan's larger or unlimited (fair-use) grant admits more — premium state comes
  only from the server entitlement. When **no** `asset.storage.bytes.max` quota governs the
  deployment the upload is ungoverned and proceeds (Core enforces only quotas that exist); when the
  quota **is** defined but the workspace holds no grant, the workspace has no allowance and the
  upload is denied — the same fail-closed contract as `session.active.max`.
- **Freeing assets restores headroom.** The reserved bytes are recorded on the asset
  (`Asset.SizeBytes`), so the host-initiated deletion (`AssetDeletionService.DeleteAsync`,
  CORE-LIFE-006) **releases** exactly those bytes back to the workspace's storage usage inside its
  deletion transaction — a clamped, idempotent decrement (a no-op for an asset with no recorded
  size). So the recorded usage reflects the workspace's **current** stored bytes rather than a
  lifetime total, exactly as `session.active.max` is consumed at start and released at end. (A
  follow-up: the background cleanup of **abandoned** never-confirmed pending intents, CORE-AST-006,
  does not yet release their reserved bytes; the host-delete path does.)

## Workspace quota release on archive (CORE-MON-007)

CORE-ENTL-004 made `workspace.active.max` consume one unit on `POST /api/v1/workspaces` for the
creating **User** subject, so a free user (`workspace.active.max = 1`) cannot create more workspaces
than their plan allows. But the symmetric **release** was missing: `ArchiveWorkspaceAsync` performed
no `QuotaEnforcementService.ReleaseAsync`, while the active-workspace list excludes archived
workspaces (`WorkspaceRepository.ListByMemberAsync` filters `Status == Active`, CORE-LIFE-009). So the
recorded usage drifted permanently **up** — a free user who created then archived their one workspace
saw an empty active list yet stayed at `used = 1` and was **locked out forever** from creating again.
This is the same "active" quota that `session.active.max` already releases on session end; the
workspace path simply never closed the loop. CORE-MON-007 closes it: archiving a workspace **releases**
its `workspace.active.max` consumption so the count tracks the user's actual active workspaces.

- **Release mirrors session end, via the existing quota services.** After the authorized, persisted
  `Active -> Archived` transition, `ArchiveWorkspaceAsync` calls the reused
  `QuotaEnforcementService.ReleaseAsync` for the `(User, workspace.active.max)` pair — the same key and
  subject kind the create consumed — exactly as `session/end` releases the `(Workspace,
  session.active.max)` slot it consumed at start. Create still consumes; archive releases; the counter
  reflects the **current** active count rather than a lifetime total. **No new table and no EF
  migration** — it reuses the existing `quota_usage` table.
- **Idempotent and fail-closed.** The release is a clamped decrement (never negative) and a no-op when
  nothing is recorded or no quota governs the deployment, so it is safe to repeat. The terminal
  archive guard (`Workspace.CanArchive`, an `Active`-only check) returns `409` for an
  already-archived workspace **before** any mutation or release, and the release runs only **after**
  the status write commits — so a concurrent second archive loses the optimistic-concurrency write
  (CORE-CONC-001) and is rejected before reaching the release. A **double archive therefore never
  double-releases**: the first archive frees exactly one unit and a second is a no-op `409`. The
  release runs only after the Owner-only authorization and the tenant/workspace existence checks
  pass, so a denied (`403`/hidden-`404`) archive frees nothing (fail-closed; threats T1/T5).
- **Subject note.** `workspace.active.max` is keyed on the **creating user** at create time; the
  workspace aggregate does not record its creator, so the release is keyed on the archiving Owner. In
  the free-tier shape this story targets (one Owner who is the creator, limit 1) these are the same
  subject. Releasing against a *different* creator (a multi-Owner organization where a second Owner
  archives a workspace a first Owner created) would require persisting the workspace creator and is
  left as a follow-up; it does not affect the free-tier lock-out this story fixes.

## Security requirements

- Never trust client-side premium flags.
- Never unlock limits before server verification succeeds.
- Store notifications must be idempotent.
- Refunds and chargebacks must revoke or downgrade entitlements.
- Grace periods must be represented explicitly.
- All purchase state changes must be auditable.
- User-visible premium state must come from server entitlements.

## Database additions

```text
plan_definitions
entitlement_definitions
subject_entitlements
quota_definitions
quota_usage
purchase_providers
purchase_transactions
purchase_events
store_notification_events
billing_account_links
```

## Definition of done

A billing/entitlement feature is not done until:

- server verifies purchase state
- quotas are enforced server-side
- downgrade/refund/cancel path exists
- duplicate notifications are ignored safely
- tests cover positive and negative entitlement cases
- docs and API contracts are updated
