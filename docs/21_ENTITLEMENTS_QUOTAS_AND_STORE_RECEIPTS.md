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
