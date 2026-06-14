# ADR 0014: Platform-Level (Tenant-Less) Audit Facts

## Status

Accepted for initial implementation. First applied by CORE-SPEC-002 (backing the entitlement/store
event catalog with real audit actions).

## Context

The append-only `audit_logs` table is the Core security audit log. Since CORE-VIS-006 / CORE-AUD-001 it
has been strictly **tenant-scoped**: every entry carries a non-null `organization_id` (a foreign key into
`organizations`), the tenant-scoped reads filter by a concrete organization id (threat T5 in
`docs/07_SECURITY_THREAT_MODEL.md`), and CORE-SEC-003 made it **tamper-evident** with a per-tenant hash
chain whose spine is a per-tenant, gap-free append `sequence` handed out by the `audit_log_sequences`
counter.

CORE-SPEC-002 must back the `audit=true` events of `csv/entitlement_event_catalog.csv` with real
`AuditAction` values that are actually emitted (the catalog is a contract, not aspirational). Several of
those events are, by deliberate design, **not tenant-scoped**:

- A **purchase** is named globally by its `(provider, provider_transaction_id)` pair and carries no
  `organization_id` (`purchase_transactions` has no tenant column; `docs/21`).
- The **entitlement** a verified purchase grants is on a `User` subject whose premium state "spans the
  deployment (it follows the user's purchase, not a tenant)" (`EntitlementSubjectType.User`).
- A **store notification** webhook is unauthenticated (a provider callback) and has no tenant context at
  all; reconciliation runs in the worker with no principal.

So `EntitlementGranted`/`EntitlementRevoked`, the `PurchaseVerification*` trio and the
`StoreNotification*` pair have **no organization** to scope an audit entry to. (`QuotaExceeded` is the
exception: a quota is denied inside an already authenticated, tenant-scoped command, so it keeps its
organization and actor and stays a normal tenant-scoped, chained fact.)

This forces a choice for how the tenant-less monetization events are audited "via a real `AuditAction`,
reusing the Audit module append path" (the story's requirement):

- **Force a tenant** onto a tenant-less concept (resolve some organization for a purchase). Wrong: it
  contradicts the documented design and there is no correct organization for a user-subject purchase or
  an anonymous webhook.
- **A second audit store** for tenant-less facts. Rejected: a parallel append-only security log
  duplicates the Audit module rather than reusing it.
- **Make the organization optional** so the existing Audit module records a tenant-less fact. Chosen.

## Decision

**The audit log records `organization_id` as OPTIONAL. A null organization is a PLATFORM-LEVEL audit
fact: a deployment-spanning, not tenant-scoped security event.** The grant/revoke, purchase-verification
and store-notification audit facts are recorded with a null organization; every existing action and
`QuotaExceeded` remain tenant-scoped with their organization set.

The change is deliberately minimal:

1. `audit_logs.organization_id` becomes **nullable** (the only schema change — one `AlterColumn`
   migration, `MakeAuditLogOrganizationOptional`). The tenant foreign key stays, now optional, so a
   **set** organization is still enforced at the row level (threat T5) and a tenant teardown still
   cascades its own audit log; a platform fact simply has no tenant key.
2. A platform fact stands **outside** the per-tenant tamper-evident hash chain (CORE-SEC-003), because
   the chain's spine is the **per-tenant** append sequence. It is appended **unsequenced** (sequence 0,
   null hashes) — the same shape an unsealed entry has — rather than forking the chain or sharing a
   bogus tenant counter. `audit_log_sequences` and the per-tenant chain are **untouched**, so the
   security-critical CORE-SEC-003 logic and its tests do not change.
3. The tenant-scoped reads (`ListByOrganizationAsync`, the paged read, the chain read) filter by a
   **concrete** organization id, so a platform fact is **never** returned through any tenant's id
   (threat T5) and never appears in a tenant's verified chain.

A platform fact's integrity posture is therefore identical to the already-shipped, append-only
`purchase_events` monetization trail (which is likewise not hash-chained): append-only at the
application layer plus the deployment's `REVOKE UPDATE/DELETE` on the table (`docs/13`). Extending the
tamper-evident chain to a platform partition (a dedicated platform sequence) is a documented,
non-blocking follow-up; it is **not** required to make the catalog a contract, which is this story's
scope.

Every platform fact stores only identifiers, enum names and generic descriptors (the subject kind/id,
the buyer as actor, the notification type or applied outcome) — never a receipt, proof, token, payload
or any content (threat T7), exactly the safe set the rest of the audit log already guarantees.

## Consequences

- The Audit module now records both tenant-scoped and platform-level security facts through **one**
  append path, so the entitlement/store catalog's `audit=true` events are backed by real, emitted
  `AuditAction` values (CORE-SPEC-002) without a parallel audit store.
- The monetization audit facts are **write-and-forensics** records (persisted, inspectable in the
  database), not surfaced through the tenant-scoped `GET /api/v1/audit-logs` read — there is no tenant to
  scope them to, which is the correct fail-closed posture (no tenant can read another's, or the
  platform's, monetization audit).
- Platform facts are not yet covered by the tamper-evident chain. This matches the existing
  `purchase_events` posture and is acceptable for the catalog-as-contract scope; a platform chain
  partition is a follow-up.
- No new route, table or event is added — only the nullable column — so the spec-consistency route,
  table, index and event checks are unchanged.
