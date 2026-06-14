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
