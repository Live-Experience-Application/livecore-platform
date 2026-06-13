# Core Epics and Stories

The sources of truth are `csv/core_epics_stories.csv` (the foundational
Phase 1 backlog) and `csv/core_phase2_epics_stories.csv` (the Phase 2
completeness, lifecycle and operations backlog). This file mirrors the `epic`
column of both; keep all three in step (see `docs/24_SPEC_CONSISTENCY.md`).

## Phase 1 epics (`csv/core_epics_stories.csv`)

1. Foundation and Repository Quality
2. Identity and Tenant Boundaries
3. Workspaces and Membership
4. Sessions and Participants
5. Scenes and Content Blocks
6. Entity System and Templates
7. Visibility and Reveal Engine
8. Realtime Event Stream
9. Asset Storage and Authorization
10. Audit, Export and Recap
11. Entitlements and Quotas
12. Store Purchase Verification
13. Store Notifications
14. Ad Eligibility
15. SDK and UI Core Packages

The entitlement, store and ad-eligibility epics (11-14) are server-side
backend modules and are sequenced before the SDK packaging epic (15) so the
TypeScript contract and SDK packages cover the billing API surface in one
pass. See `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`,
`docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md` and ADRs 0010-0011.

## Phase 2 epics (`csv/core_phase2_epics_stories.csv`)

These epics complete, harden and operationalize the Phase 1 platform. They are
listed in backlog order, not as a numeric continuation of the Phase 1 list.

1. API Completeness
2. Reveal Lifecycle
3. Resource Lifecycle and Deletion
4. Session Event Stream Completeness
5. Production Operations Readiness
6. Observability
7. Worker Background Jobs
8. Specification Hygiene

Use one story per PR.
