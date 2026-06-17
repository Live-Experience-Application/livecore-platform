# Security Threat Model

## Primary security promise

A participant must never receive content they are not authorized to see.

This includes REST API responses, realtime events, asset metadata, asset downloads, search results, exports and offline cache payloads.

## Main threats

### T1: Broken object-level authorization

Risk:

- user guesses resource ID and reads another workspace resource

Controls:

- organization + workspace + resource authorization on every endpoint
- negative tests for foreign IDs
- no direct repository access from controllers

### T2: Visibility leak through API

Risk:

- API returns hidden content and UI hides it

Controls:

- server-side filtering
- participant DTOs exclude hidden fields
- tests compare Host response vs Participant response

### T3: Realtime event leak

Risk:

- WebSocket group receives event for wrong participant

Controls:

- recipient calculation in Visibility module
- per-recipient event projection
- reconnect replay filters events again

### T4: Asset leak

Risk:

- private asset is reachable via static URL

Controls:

- private buckets only
- signed URL after authorization
- no public object listing
- short-lived URLs

### T5: Tenant isolation failure

Risk:

- data from organization A is visible to organization B

Controls:

- organization_id on tenant-scoped tables
- composite indexes with organization_id/workspace_id
- authorization middleware and tests

### T6: Invite abuse

Risk:

- leaked invite token grants unwanted access

Controls:

- scoped invite tokens
- expiry
- revocation
- role limitation
- audit logs

### T7: Log leaks

Risk:

- private scene content appears in logs

Controls:

- structured logs with IDs, not sensitive body content
- redaction policies

### T8: Export leak

Risk:

- participant export includes hidden content

Controls:

- export role-based projection
- explicit host/admin export scopes

### T9: Abuse / denial of service

Risk:

- an unauthenticated client floods the anonymous store-notification webhooks (each call does database work and
  runs an external parser — a DoS and ledger-amplification surface), or probes invite-token / organizationSlug
  enumeration
- an authenticated client hammers the API with no per-caller ceiling

Controls:

- ASP.NET Core rate limiting (`UseRateLimiter`, CORE-SEC-001): a strict per-IP limit on the anonymous webhooks
  and a per-principal global limit on the authenticated surface; excess requests get `429`
- a hard request-body-size cap on the anonymous webhooks beyond the application-level payload cap
- all limits configurable; `429` Problem Details carry no tenant/principal/resource detail (T7)
- the limiter emits the IETF draft `RateLimit-Limit`/`RateLimit-Remaining`/`RateLimit-Reset` headers on an
  admitted response and on the `429` (with `Retry-After`), so a browser SDK can back off before it is throttled
  (CORE-DX-005); the headers are numeric ceilings/counts only — never a tenant, principal or resource identifier
  (T7) — and the CORS policy exposes them (with `ETag`, `Location` and the `X-Request-Id` correlation header)
  via `Access-Control-Expose-Headers` so a cross-origin browser consumer can read them without widening any
  server-side authorization

## Audit log integrity (CORE-SEC-003)

The audit log is the security record of who did what; if it can be altered silently it cannot be trusted. The
log is append-only at the application level (an immutable aggregate, an append + read repository with no
update/delete path), and it is additionally **tamper-evident**: every entry is sealed into a per-tenant
SHA-256 **hash chain** (`sequence` + `previous_hash` + `entry_hash`), so altering, deleting, inserting or
reordering a persisted row breaks the chain and the `AuditLogChainVerifier` routine detects it and pinpoints the
first broken entry (tenant-scoped — a break in one tenant never implicates another).

Controls:

- a per-tenant hash chain over `audit_logs` and a verification routine (detection)
- a deployment step that REVOKEs `UPDATE`/`DELETE` on `audit_logs` from the runtime application role
  (prevention; `docs/13_SELF_HOSTING_REQUIREMENTS.md`)
- the chain is unsigned (no secret), so cryptographic signing/external anchoring against a fully privileged
  actor is a documented follow-up

## Data-subject erasure (CORE-PRIV-001)

A data subject has a right to erasure (GDPR Art.17), but until this story Core had **no** erasure path: the
user-profile repository exposed only find/add/update, and the subject's personal data was spread across the
schema — the `users` profile (OIDC subject, email, display name), `participants.display_name` and
`workspace_invitations.invited_email` (both plaintext). The schema already **assumed** user deletion
(`assets.created_by`, `export_jobs.requested_by` and `participants.user_id` are nullable `ON DELETE SET NULL`;
`organization_members.user_id` and `workspace_members.user_id` are `ON DELETE CASCADE`), yet nothing ever
deleted a `users` row.

The erasure command (`DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data`,
Owner/Admin) closes that gap:

- it HARD-deletes the subject's `users` profile row (the row IS the PII), and ANONYMIZES the personal data the
  profile's deletion cannot itself reach — `participants.display_name` (scrubbed to a fixed placeholder, the
  user link cleared) and `workspace_invitations.invited_email` (scrubbed to a non-routable placeholder);
- deleting the profile lets the database honor the foreign keys above: the subject's exports/assets **survive
  anonymized** (`SET NULL` creator) and their memberships are revoked (`CASCADE`);
- the operation is tenant-scoped in its **authorization** (an Owner/Admin of the resolved tenant acting on a
  member of it, fail-closed: `403` for a non-privileged member, hidden `404` for a foreign-tenant/unknown
  member — threats T1/T5), but its **effect** is global because the user profile is one deployment-wide identity:
  the subject's personal data is erased everywhere it was stored;
- it is **audited by id only** (`UserProfileErased`, actor + erased subject id, never the erased PII), and the
  audit log's references are recorded facts, not foreign keys, so the **PII-free append-only hash chain still
  verifies** after an erasure — this is exactly what makes the right to erasure reconcilable with the immutable
  audit log (the audit-log integrity control above);
- it **fails closed** on the orphan invariant: the sole Owner of an organization cannot be erased (erasing them
  would cascade-remove their membership and leave the tenant permanently unreachable), returning `409` and
  changing nothing.

The controller/processor split for self-hosters, the data-residency configuration, the default retention
windows and the at-rest-encryption expectations are recorded in the privacy/data-protection documentation
(CORE-PRIV-005).

## Authorized tenant organization deletion (CORE-PRIV-002)

A tenant has a right to offboarding / data deletion, but until this story Core had **no** path to delete a
tenant: `csv/api_routes.csv` exposed only member and template deletion, the `OrganizationRepository` had no
delete, and so the `ON DELETE CASCADE` foreign keys every tenant-scoped table already declares into
`organizations(id)` (workspaces, sessions, participants, memberships, the audit log, and the rest —
`docs/10_DATABASE_SCHEMA.md`) were **unreachable**. The whole tenant teardown was designed into the schema but
nothing could trigger it.

The deletion command (`DELETE /api/v1/organizations/{organizationSlug}`, Owner only) closes that gap:

- it HARD-deletes the `organizations` root row, and the database's existing `ON DELETE CASCADE` foreign keys
  then remove the whole tenant in the same operation — its workspaces, sessions, participants, memberships and
  its OWN audit log (the audit log is intentionally part of the tenant teardown, so an offboarded tenant leaves
  no tenant-scoped data behind);
- it is the **most destructive** tenant action, so it is **Owner-only** — strictly narrower than member
  management or erasure (Owner/Admin). The authorization is tenant-scoped and fail-closed (threats T1/T5): a
  non-Owner tenant member — an Admin included — is denied `403`, and a foreign-tenant/unknown organization is
  hidden as `404` (the tenant context resolver requires the token's organization claim AND a persisted Owner
  membership);
- it is **audited at the platform level**. The deleted tenant's own audit log is cascade-removed, so a
  tenant-scoped record would be torn down with it; the offboarding is recorded as a PLATFORM-LEVEL
  `OrganizationDeleted` audit fact (a null organization, **outside** the per-tenant hash chain — the same
  posture as the entitlement/store facts) that SURVIVES the teardown. It records the actor (the Owner) and the
  deleted organization by id only, never the tenant's name or any content (threat T7), and the deleted-org id is
  a recorded fact (not a tenant foreign key), so it is not cascade-removed;
- the audit append and the cascade delete commit in **one transaction**, so the teardown is applied whole or
  not at all (a failure leaves the tenant intact and writes no audit row).

The orphaned-residue limitation (an organization-subject entitlement/quota row is keyed by a polymorphic
subject pair with no organization foreign key, so it is not reached by the tenant cascade) is recorded in
`docs/10_DATABASE_SCHEMA.md`.

## Supply chain integrity (CORE-DEP-003)

The published API, worker and migrations images are part of the trusted computing base a deployment runs. An
immutable, versioned **release tag** (CORE-OPS-009) fixes what is pulled, but the layers underneath it can still
drift — a floating base-image tag is re-published over time, an unpinned dependency restore resolves differently,
and a known-CVE base image ships unnoticed.

Risk:

- a base image floats (`mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0`), so a rebuild of the same release version
  silently changes the underlying layers
- a known critical vulnerability in a base image is published without any gate catching it
- no bill of materials exists for a shipped image, so an operator cannot tell what is inside it

Controls (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, "Pinned base images, SBOM and vulnerability scan"):

- base images pinned by **immutable digest** in all three Dockerfiles, so a release always builds on the exact
  same layers and an upstream re-tag cannot drift into it
- a **reproducible** NuGet restore: every project commits `packages.lock.json` and the image builds restore in
  locked mode, so the dependency graph cannot float
- the publish job produces a CycloneDX **SBOM** and a **CVE scan** for each image and runs a **fail-closed gate**
  (`scripts/assert-image-scan.ps1`) that **blocks the push on a critical vulnerability** or a missing SBOM; the
  gate decision is unit-tested from a seeded critical CVE (`scripts/test-image-scan.ps1`)
- the existing immutable-tag guard is unchanged (a published version is never overwritten)
- cryptographic build provenance/attestation (e.g. cosign) is a documented follow-up (it needs signing-key
  management out of scope here)

## Required test categories

- foreign workspace ID denial
- hidden content denial
- reveal to selected participants only
- asset download denial
- realtime event recipient filter
- reconnect replay filter
- audit creation for visibility changes
