# Product Vision and Scope - Core

## Vision

LiveCore is a self-hostable platform for controlled interactive live sessions.

It enables a host to control what information is visible to which participants, when, and under what context.

## Core product statement

> A product-neutral Live Experience Platform for scene-based sessions, role-aware content delivery, realtime reveals, audit logs and reusable vertical templates.

## Core user roles

Generic roles only:

```text
Owner
Admin
Host
CoHost
Participant
Observer
Auditor
ServiceAccount
```

Verticals may rename roles in their UI, but Core stores and enforces only generic roles.

## In scope

- organizations
- workspaces
- memberships
- sessions
- participants
- scenes
- content blocks
- generic entities
- entity types
- assets
- visibility rules
- reveal events
- session event stream
- audit logs
- exports
- basic recaps
- templates
- entitlements and quotas
- purchase verification and store receipts
- the purchase-to-entitlement grant chain (a verified purchase grants the buyer a SubjectEntitlement; refunds and cancellations revoke or downgrade)
- ad eligibility
- OIDC authentication
- server-side authorization
- Docker/self-hosting readiness

## Out of scope

- DnD rules
- Pen-and-Paper terminology
- enterprise training terminology
- marketplace
- payment processing and storefront/paywall UI
- native mobile apps
- AI-generated content in Core v1
- full offline multiplayer sync
- 3D or map engine

## Monetization (v1)

Mobile monetization is **in scope for Core v1**. Core verifies store purchases
server-side, grants the resulting entitlements, revokes or downgrades them on
refund/cancellation/chargeback, and enforces free-tier quotas server-side so a
mobile client cannot bypass limits or premium state. Core stays product-neutral
and never processes payments, renders a paywall/store, or displays ads — those
remain in the mobile vertical (`docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md`).

The authoritative v1 monetization scope and acceptance is recorded once in
`docs/24_SPEC_CONSISTENCY.md` ("Decision recorded (CORE-MON-001)"); the design
lives in `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md` and
`docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md`, and the delivering stories are
the Monetization v1 epic (CORE-MON-001..010) in
`csv/core_phase3_epics_stories.csv`.

## Product quality target

This is production-ready software. Every feature must be designed for maintainability, security, observability and upgradeability from the beginning.
