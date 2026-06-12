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
hosted_sessions.ads.disabled = true if table pass included
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
