# Core Epics and Stories

The source of truth is `csv/core_epics_stories.csv`.

Epics:

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
16. Production Operations Readiness

The entitlement, store and ad-eligibility epics (11-14) are server-side
backend modules and are sequenced before the SDK packaging epic (15) so the
TypeScript contract and SDK packages cover the billing API surface in one
pass. See `docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md`,
`docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md` and ADRs 0010-0011.

Use one story per PR.
