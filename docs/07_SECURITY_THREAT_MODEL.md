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
- a per-IP limit on the anonymous NON-webhook surface — the `/hubs/session` negotiate and any anonymous REST
  probe — so an unauthenticated flood is bounded too, while the infrastructure endpoints (`/health/*`,
  `/metrics`) opt out so probes/scrapes stay reachable (CORE-SEC-007)
- a hard request-body-size cap on the anonymous webhooks beyond the application-level payload cap
- the anonymous webhook additionally keeps a NON-DISABLEABLE request-rate floor: turning the limiter off
  (`RateLimiting:Enabled=false`) drops it to a generous floor rather than removing the limit, so disabling the
  global limiter can never fully remove webhook volume protection (the body-size cap likewise survives;
  CORE-SEC-007)
- all limits configurable; `429` Problem Details carry no tenant/principal/resource detail (T7)
- the limiter emits the IETF draft `RateLimit-Limit`/`RateLimit-Remaining`/`RateLimit-Reset` headers on an
  admitted response and on the `429` (with `Retry-After`), so a browser SDK can back off before it is throttled
  (CORE-DX-005); the headers are numeric ceilings/counts only — never a tenant, principal or resource identifier
  (T7) — and the CORS policy exposes them (with `ETag`, `Location` and the `X-Request-Id` correlation header)
  via `Access-Control-Expose-Headers` so a cross-origin browser consumer can read them without widening any
  server-side authorization

## Log redaction enforced as a guardrail (CORE-OBS-006)

T7's control is "structured logs with IDs, not sensitive body content". Until this story that was
**reviewer discipline only**: the Core uses no `Microsoft.Extensions.Compliance.Redaction`, no
`[DataClassification]`/`[LogProperties]` and no scrub layer (grep zero), and the existing logs are correctly
ID-only (for example `StoreNotificationReconciliationService`, `ExportProcessingService`,
`DataRetentionSweepService` — provider/job/resource **ids** and counts, never a transaction id, an email, a
display name or any content). Nothing mechanically stopped a future content-bearing log statement from
shipping, so the T7 logging promise rested on a human noticing it in review.

This story makes ID-only structured logging a **build guardrail**, enforced exactly like the boundary scan and
the destructive-migration lint (a PowerShell analysis module + a CI lint + a gate-logic test). The analysis
(`scripts/LiveCoreLogRedaction.psm1`) is C#-aware: it masks comments and string CONTENT before locating calls
and balancing parentheses, so a logger call in a comment or a string is never mistaken for one and a template
spanning several lines is handled. The `log-redaction` CI job runs the gate-logic test
(`scripts/test-log-redaction.ps1`) and then the lint (`scripts/lint-log-redaction.ps1`) over the tracked
first-party `apps/` C# source (the generated EF migrations excluded). The lint **fails the build** on an
`ILogger` call (`LogTrace`/`LogDebug`/`LogInformation`/`LogWarning`/`LogError`/`LogCritical`, the generic
`Log`, and `BeginScope`) whose message TEMPLATE would put a value into the log text rather than a structured
identifier:

- **InterpolatedTemplate** — the call uses an interpolated string (`$"...{x}..."`), which embeds the runtime
  value directly in the message text and cannot be redacted (the CA2254 anti-pattern). Flagged anywhere in a
  log call's arguments.
- **ConcatenatedValue** — the template concatenates a non-literal expression into the message
  (`"user " + name + " ..."`). A constant concatenation of string literals (`"a" + "b"`, the existing
  multi-line `LogCritical` startup template) is a normal template and is allowed.
- **ForbiddenPlaceholder** — the constant template names a structured property for content/PII/a secret
  (`{Email}`, `{DisplayName}`, `{AccessToken}`, `{InviteToken}`, `{ContentBody}`, `{Password}`, ...), so the
  value WOULD be captured as a log property.

Existing ID-only logs are **unaffected**: a placeholder whose last word is an identifier/metadata suffix
(`Id`, `Count`, `Type`, `Status`, `Code`, `Version`, `Slug`, `Length`, ...) is always allowed — so
`{ExportJobId}`, `{ItemCount}`, `{ResourceType}`, `{ContentBlockId}` and `{OrganizationSlug}` pass — and coarse
names that are not content/PII/secret vocabulary (`{Provider}`, `{JobName}`, `{HeartbeatFile}`,
`{RequestRoute}`, `{MissingSettings}`, `{Window}`, `{Reason}`) pass too. The required test proves both
directions: a seeded content-bearing/interpolated template is flagged, and every one of the tracked source's
existing ILogger calls passes. This complements the request-scope log context (CORE-OBS-002), which already
carries only identifiers and the principal subject, never content. See `docs/15_OBSERVABILITY.md`.

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

## Audit log tamper-proofing in code (CORE-SEC-004)

CORE-SEC-003 made the audit log tamper-**evident** (the hash chain DETECTS a mutated, deleted, inserted or
reordered row) but, out of the box, not tamper-**proof**: the per-tenant `AuditLogChainVerifier` is the
detection control, and the REVOKE was operator documentation only, so a regression inside the running process —
a read path that forgot `AsNoTracking`, or new code that re-pointed the table — could still have `SaveChanges`
issue an `UPDATE`/`DELETE` against an audit row, and a deployment that never ran the REVOKE left the runtime
role free to do the same at the database. This story closes both gaps so the audit log cannot be mutated from
inside the running process at all, with three defence-in-depth layers UNDER the existing chain (which still
detects anything that bypasses them):

- **Non-tracked reads.** The three audit read paths (`AuditLogRepository.ListByOrganizationAsync`,
  `ListPageByOrganizationAsync`, `ListChainByOrganizationAsync`) read with `AsNoTracking`, so a caller (and the
  chain verifier) gets DETACHED entities. There is nothing tracked to write back, so a read followed by a stray
  mutation + `SaveChanges` persists nothing.
- **A fail-closed persistence interceptor.** `AuditLogTamperProtectionInterceptor` (an EF Core
  `SaveChangesInterceptor` wired on every runtime context — the API host and every worker job — via
  `UseLiveCoreNpgsql`) inspects the change tracker on every `SaveChanges` and THROWS `AuditLogTamperException`
  if any `AuditLogEntry` is `Modified` or `Deleted`, aborting the whole save before it reaches the database. An
  append (`Added`) and the reads are untouched. A raw-SQL `UPDATE`/`DELETE` does not pass through `SaveChanges`,
  so it is intentionally not caught here — that is the REVOKE's and the chain's job.
- **A checked-in REVOKE migration.** `RevokeAuditLogMutationFromRuntimeRole` turns the previously
  documentation-only deployment step into an automated one: when the operator names the runtime role in the
  `livecore.audit_log_app_role` database setting it `REVOKE`s `UPDATE`/`DELETE` on `audit_logs` from that role
  (re-granting `INSERT`/`SELECT`); when unset it is a safe no-op (`docs/13_SELF_HOSTING_REQUIREMENTS.md`).

The existing CORE-SEC-003 append path and per-tenant hash chain are unchanged and keep working: the verifier
still verifies a clean chain and still detects a tampered or deleted row.

## Audit appends inside a unit of work (CORE-CONC-008)

The per-tenant hash chain (CORE-SEC-003) is only fork-proof if the append's `audit_log_sequences` row lock is
held until the insert commits. That lock lives only as long as its enclosing transaction, so the allocation, the
previous-hash read and the insert must run inside ONE transaction. `AuditLogRepository.AppendAsync` was atomic
when it ran INSIDE a CORE-CONC-002 unit of work (the reveal/hide, session, deletion, retention and store
commands), but the previously DIRECT appends — an organization/workspace member change, a personal-data export,
a quota-exceeded fact — allocated the sequence in its own auto-committed statement, releasing the lock BEFORE the
insert. Two concurrent same-tenant appends could then each read a predecessor the other had not committed yet and
seal off a FORKED chain — which the `AuditLogChainVerifier` later reports as tampering: a **false positive that
masks real detection**.

This story closes that window by making the append itself transactional, paired with CORE-SEC-004:

- **Every append runs inside a unit of work.** `AuditLogRepository.AppendAsync` now reuses the CORE-CONC-002
  `TransactionalUnitOfWork`: when a command has already opened a transaction on the context it ENROLS in it
  (unchanged behaviour); otherwise it opens its OWN unit of work, so the sequence allocation, the previous-hash
  read and the insert always commit together. The row lock is held until the insert commits, so a concurrent
  same-tenant append blocks on it and chains off the just-committed predecessor instead of forking. A platform-
  level (null-organization) fact joins no chain, so it is simply inserted; the tenant-scoped reads still never
  return it.
- **Correct under retry.** Because the append now runs inside the retrying execution strategy (CORE-CONC-003), a
  transient failure rolls the attempt's sequence allocation back and re-runs the whole append; the entry's
  rolled-back chain linkage is discarded so the retry re-seals with the freshly re-allocated sequence and the
  predecessor visible now (the chain stays gap-free).

The result: many concurrent same-tenant audited actions produce a single unbroken, gap-free hash chain — no
fork, and no false-positive tamper detection.

## Streamed audit-chain verification (CORE-PERF-005)

The per-tenant hash chain (CORE-SEC-003) is the security record's tamper-evidence control, and the
`AuditLogChainVerifier` is the routine a deployment runs to check it. But the chain GROWS without bound as a
tenant accumulates audited actions, and the verifier previously read it through
`AuditLogRepository.ListChainByOrganizationAsync`, which materializes a tenant's ENTIRE chain into memory in one
read. So the cost of asking "has this tenant's audit history been altered?" grew with the log: a long-lived,
high-activity tenant could force the verification to load an unbounded result set, which both wastes memory and —
because integrity checks are exactly the thing an operator wants to be able to run routinely — is itself a
scale/abuse surface (threat T9).

This story makes verification stream in BOUNDED segments without changing what it detects:

- **Ordered segments via the `(organization_id, sequence)` cursor.** The verifier reads the chain through
  `AuditLogRepository.ListChainSegmentByOrganizationAsync`, the cursored, bounded counterpart of the full read:
  `WHERE organization_id = X [AND sequence > cursor] ORDER BY sequence LIMIT n`, backed by the unique
  `audit_logs(organization_id, sequence)` index. It walks the chain one fixed-size window at a time, advancing the
  cursor to the last entry of each segment, so at most one segment (plus O(1) running state) is ever held —
  verification memory and time stay bounded as the audit log grows. This mirrors the Realtime reconnect-replay
  cursor (CORE-PERF-002) and is tenant-scoped exactly like every other audit read (the predicate leads with
  `organization_id`, so a foreign tenant's records are never read — threat T5).
- **Detection is unchanged.** The per-entry checks (content integrity, linkage, contiguity) live in one
  incremental accumulator (`AuditLogChainVerification`) that the streamed segments feed one entry at a time; the
  in-memory `AuditLogChain.Verify` drives the SAME accumulator over a materialized list, so the streamed and the
  materialized reads cannot drift apart. A tampered, deleted, inserted or reordered chain is still detected and
  the result still pinpoints the FIRST broken entry — including a break that lies several segments in — and the
  verifier stops reading further segments at the break (later entries cannot change the verdict). The result
  carries only identifiers and counts, never recorded content (threat T7).

The append path, the per-tenant chain and the `AuditLogChainVerifier` contract are otherwise unchanged; this is a
read-shape change to the verification routine. It pairs with CORE-SEC-004 (the read stays non-tracked, so a
verification can never write a row back) and CORE-PERF-003.

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
(`docs/25_PRIVACY_AND_DATA_PROTECTION.md`, CORE-PRIV-005).

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

## Data-subject access and portability export (CORE-PRIV-004)

A data subject has a right of access (GDPR Art.15) and a right to data portability (Art.20), but until this
story Core had **no** path to assemble a subject's personal data for them: the only self-service route was
`GET /api/v1/me`, which returns the caller's *principal context* (their profile id and the memberships they hold)
— not the personal data Core actually stores about them, which is spread across the schema (the `users` profile,
`organization_members`/`workspace_members` roles, `participants.display_name` records and the
`workspace_invitations.invited_email` rows). The erasure command (CORE-PRIV-001) could already REMOVE that data,
but nothing could READ it back.

The export command (`GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export`)
closes that gap and is the read-side counterpart of erasure:

- it assembles a **machine-readable** export of the documented personal-data set — the subject's identity profile
  (id, OIDC issuer/subject, display name, email) plus their organization membership, their workspace
  memberships, their participant records and the invitations addressed to their email — reusing the same
  user-profile/membership/participant/invitation repositories the erasure command uses (no parallel persistence
  path);
- it is **distinct from the session/workspace Exports feature** (`Exports` module, `export_jobs`): that exports
  content artifacts a workspace produced; this discloses ONE subject's personal data for an Art.15/20 request;
- it is **tenant-scoped** in BOTH authorization and data. Unlike erasure — whose EFFECT is global because a
  subject's personal data must be erased everywhere (Art.17) — the export resolves a single tenant and every
  collection is read scoped to that organization (the repositories lead their predicates with `organization_id`).
  So an Owner/Admin exporting on the subject's behalf never learns of the subject's activity in a tenant they do
  not control, and the subject reaches their data in other tenants only through those tenants' own export routes
  (threat T5). The one GLOBAL datum is the subject's own user profile (a single deployment-wide identity) —
  disclosed to the subject themselves or the tenant's data controller acting for them, exactly what Art.15
  requires;
- the **PII is delivered only to the entitled recipient**: the data subject THEMSELVES (self-service — the caller's
  resolved user profile matches the target member's subject) OR an Owner/Admin acting on their behalf. Both paths
  are tenant-scoped and fail-closed (threats T1/T5): a non-privileged tenant member who is not the subject — a
  Host/CoHost/Participant/Observer/Auditor — is denied `403`, and a foreign-tenant/unknown organization or member
  is hidden as `404` (the tenant context resolver requires the token's organization claim AND a persisted
  membership, exactly as the erasure route does);
- it is **audited by id** (`PersonalDataExported`, actor + exported subject id, in the tenant): disclosing
  personal data is security-relevant, so the access is recorded — but the audit row carries ONLY identifiers,
  never the disclosed email, display names, invited emails or any of the exported data (threat T7). The PII lives
  only in the export RESPONSE; the **PII-free append-only hash chain still verifies** after an export, and the
  audit row outlives a later erasure of the same subject (the references are recorded facts, not foreign keys) —
  the same posture that makes erasure reconcilable with the immutable audit log;
- the export response never carries a **secret**: no invitation token or token hash, no access token and no OIDC
  credential (threats T6/T7). It carries the subject's personal data (the point of the export) and nothing
  belonging to another subject or another tenant.

The data-residency configuration, the controller/processor split for self-hosters and the retention windows
remain in the privacy/data-protection documentation (`docs/25_PRIVACY_AND_DATA_PROTECTION.md`, CORE-PRIV-005).

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
- cryptographic build provenance is now in place: each pushed image is **signed** and its CycloneDX SBOM attached
  as a verifiable **attestation** with keyless cosign, gated fail-closed by `cosign verify` /
  `cosign verify-attestation` against the published digest (CORE-SEC-008, below)

## Signed images and SBOM attestations (CORE-SEC-008)

CORE-DEP-003 fixes *what is inside* a published image and that it carries no known-critical vulnerability, but it
left a gap a self-hoster cares about: the image could not be proven to have been **built by this pipeline**. The
SBOMs were only an `actions/upload-artifact` build artifact (misleadingly named), never a signature or a
verifiable attestation bound to the image in the registry. So a `ghcr.io` image's provenance was unprovable.

Risk:

- a tampered or look-alike image is passed off as an official release because nothing cryptographically binds a
  published image to the workflow that built it, and the SBOM is a detached artifact a third party can fabricate

Controls (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, "Signed images and SBOM attestations"):

- on a release tag, **after the CVE gate passes and the images are pushed**, the `publish` job runs **keyless**
  `cosign sign` against each published **digest**: a short-lived GitHub Actions OIDC token has Sigstore **Fulcio**
  issue a throwaway certificate bound to the workflow identity and **Rekor** log the signature, so there is **no
  private key** to store or leak
- `cosign attest` attaches the **same CycloneDX SBOM** produced for the CVE gate as an in-toto **attestation**
  (`--type cyclonedx`) bound to the digest, so the bill of materials travels with the image, signed
- the only added privilege is **`id-token: write`, scoped to the `publish` job only** (the workflow default is
  `contents: read`), so no other job can mint an OIDC token
- verification **fails closed**: `cosign verify` and `cosign verify-attestation` run against the published digest
  (asserting the GitHub OIDC issuer and this repo's release-workflow identity), then a fail-closed gate
  (`scripts/assert-image-attestation.ps1`) runs over the output, so a missing, empty, wrong-predicate or
  unverifiable signature/attestation fails the release. The decision is pure logic
  (`scripts/LiveCoreImageAttestation.psm1`) tested from seeded fixtures (`scripts/test-image-attestation.ps1`),
  and the `publish-dry-run` job mirrors the whole round-trip against a locally-built digest with a throwaway key
  (no OIDC, no push), including a negative check that an unsigned image fails verification
- this is the container-image analogue of the npm build provenance the `@livecore/*` packages carry (CORE-PUB-004)

## Static analysis of first-party code (CORE-SEC-006)

CORE-DEP-003 scans the published images' DEPENDENCIES for known vulnerabilities, and the lock files pin them
(CORE-CMP-002), but until this story nothing statically analyzed the C# and TypeScript the project ITSELF writes.
First-party code was never security-analyzed, so a newly introduced injection, a cleartext-secret write or a
similar high-severity defect in the sources had no automated tripwire - it rested on author and reviewer
discipline alone, exactly the gap the boundary scan, the log-redaction guardrail and the coverage gate each close
for their own concern.

Risk:

- a high/critical security defect (an injection, path traversal, cleartext storage of a secret, unsafe
  deserialization, ...) is introduced into the first-party C# or TypeScript and merges or ships unnoticed because
  no static analysis runs over the code

Controls (`docs/14_TESTING_STRATEGY.md`, "Static analysis (SAST) and the CI gate"):

- the CI `codeql` job runs **CodeQL** (GitHub's first-party SAST engine) over BOTH first-party languages on every
  push and pull request - C# in `manual` build mode against the same `dotnet build` the `dotnet` job runs, and
  TypeScript buildless (`build-mode: none`)
- a **fail-closed severity gate** (`scripts/assert-codeql-findings.ps1`) **fails the build** on any result whose
  rule scores `security-severity >= 7.0` (the high/critical band, GitHub's documented CVSS mapping); a
  medium/low or a non-security finding is reported but not blocking. The gate decides over CodeQL's SARIF
  LOCALLY (`upload: false`), so it needs no GitHub Advanced Security and cannot be silently disabled by a
  repository setting; a missing, empty or malformed SARIF blocks rather than passing
- the gate DECISION is unit-tested from seeded SARIF fixtures (`scripts/test-codeql-gate.ps1`, run first in the
  job): a seeded high/critical finding fails closed, a clean/medium/low one passes, the floor is configurable
  and a malformed/non-SARIF document is rejected - the "fails closed on a high/critical finding" proof, without
  committing a real vulnerability to obtain it
- the job is wired into the SAME required-checks set the release jobs depend on (`publish`/`publish-packages`
  `needs:` every gate including `codeql`), so a high/critical finding blocks not only the merge signal but,
  transitively, any image push or npm publish
- broadening the query suite (for example `security-extended`) and uploading the SARIF to GitHub code scanning
  for triage (when Advanced Security is enabled) are documented options that do not change the fail-closed gate

## Transport security: HSTS and HTTPS redirection (CORE-SEC-005)

The Core API is documented to run BEHIND a TLS-terminating reverse proxy (CORE-OPS-003;
`docs/13_SELF_HOSTING_REQUIREMENTS.md`), where the public HTTPS boundary — the `http`→`https` redirect and the
`Strict-Transport-Security` (HSTS) header — lives at the edge and the app sees the real client scheme through the
trusted proxy's `X-Forwarded-Proto` (restored by `UseForwardedHeaders`, which runs FIRST in the pipeline). But
before this story the host wired NEITHER `UseHsts` NOR `UseHttpsRedirection`, so TLS/HSTS was entirely delegated
to an edge the app cannot verify exists: a deployment that runs WITHOUT a terminating proxy — terminating TLS in
Kestrel directly — had no app-level transport-security defense at all.

Risk:

- a deployment that is not behind a TLS-terminating proxy serves the API with no `http`→`https` redirect and no
  HSTS header, so a client can be downgraded to or kept on plain `http` (a man-in-the-middle / SSL-strip surface)

Controls (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, "App-level HSTS and HTTPS redirection"):

- ASP.NET Core's built-in middleware (`Microsoft.AspNetCore.HttpsPolicy`, part of the shared framework — NO new
  dependency), each guarded by its own configuration toggle under `HttpsSecurity:*`:
  - `UseHttpsRedirection` redirects an insecure `http` request to `https`; because `UseForwardedHeaders` runs
    first, behind a trusted terminating proxy the app already sees `Request.Scheme == "https"`, so the redirect
    does NOT fire — the proxy posture is unchanged (no double-redirect, no fight with the edge);
  - `UseHsts` emits `Strict-Transport-Security` on a secure response (the framework's default excluded loopback
    hosts are left intact, so local development is never pinned to `https`);
- both toggles are **fail-safe OFF by default**, so the documented proxy posture (the proxy terminates TLS and
  owns the redirect/HSTS) is the unchanged default; a deployment that terminates TLS in the app itself enables
  them, so the toggles are disabled only where the documented proxy terminates TLS;
- every value is read from configuration only (`HttpsSecurity:*`), and a misconfigured max-age / redirect status
  / port falls back to the safe default rather than crashing the host or producing a degenerate redirect;
- the header and the redirect carry no tenant/principal/resource detail (threat T7), and transport security is a
  coarse edge defense layered ON TOP OF the OIDC/tenant authorization every endpoint already enforces — it never
  widens authorization, exactly like the CORS allow-list and the rate limiter.

## Closing the rate-limiting gaps on anonymous surfaces (CORE-SEC-007)

CORE-SEC-001 bounded the two highest-risk surfaces — the anonymous store-notification webhooks (a strict per-IP
limit plus a hard body-size cap) and the authenticated surface (a per-principal global limit). But two gaps
remained on the **anonymous** side, both abuse/DoS surfaces (threat T9):

- the per-principal global limiter left **every other anonymous surface** unthrottled — most notably the
  `/hubs/session` SignalR **negotiate** (which an unauthenticated client can POST to repeatedly) and any
  anonymous REST probe (invite-token / `organizationSlug` enumeration), since "no principal" resolved to
  "no limiter";
- turning the limiter off (`RateLimiting:Enabled=false`, for a deployment that throttles at its edge) made
  **both** limiters no-ops, so it removed the webhook's request-rate protection entirely — only the body-size
  cap survived.

This story closes both, reusing the existing fixed-window per-IP machinery (no new limiter type, no new
dependency):

- **A per-IP limit on the anonymous non-webhook surface.** The global limiter now partitions an anonymous
  request **per client IP** (the real client restored by `UseForwardedHeaders` from a trusted proxy) instead of
  leaving it unthrottled, so a flood of the negotiate or an anonymous probe is bounded with `429`. The anonymous
  webhook is left to its own named per-IP policy (not double-charged), and the infrastructure endpoints
  (`/health/*`, `/metrics`) opt OUT with `DisableRateLimiting` so orchestrator probes and Prometheus scrapes from
  a single source are never throttled into a restart loop.
- **A non-disableable webhook floor.** The webhook's named policy no longer becomes a no-op when the limiter is
  disabled; it drops to a generous, configurable-but-non-disableable request-rate **floor** (a non-positive
  configured floor falls back to the default, so it can never be set away to nothing). Together with the
  body-size cap — which was already independent of the toggle — disabling the global limiter can never fully
  remove webhook volume protection.

Every limit stays runtime configuration (`RateLimiting:Anonymous:*`, `RateLimiting:Webhooks:Floor*`;
`docs/13_SELF_HOSTING_REQUIREMENTS.md`) with safe, generous defaults, and the `429` Problem Details (and the
`RateLimit-*` headers) carry no tenant, principal or resource detail — a generic message and a numeric ceiling
only (threat T7). Rate limiting remains a coarse abuse ceiling layered ON TOP OF the OIDC/tenant authorization
every endpoint already enforces; it never widens authorization.

## Baseline HTTP security response headers (CORE-SEC-009)

CORE-SEC-005 added the transport-security headers (HSTS / HTTPS redirect), but the API still served every
JSON and Problem Details response with NO content-handling defense: the pipeline (`apps/api/Program.cs`) wired
no response-header middleware — only Kestrel's `AddServerHeader = false` — so a response carried no
anti-sniffing, no referrer policy and no framing/embedding defense.

Risk:

- a browser MIME-sniffs an API response into a type the server never declared, or embeds the responses in a
  frame, or leaks the request URL (which can carry a resource id) through the onward `Referer`

Controls (`docs/13_SELF_HOSTING_REQUIREMENTS.md`, "Baseline HTTP security response headers"):

- a small configurable response-header middleware (`SecurityHeadersMiddleware`) adds to EVERY API, error and
  SignalR-negotiate response — and a 404/406/500 alike — three baseline headers:
  - `X-Content-Type-Options: nosniff` — stop MIME content-sniffing;
  - `Referrer-Policy: no-referrer` — keep the request URL out of the onward `Referer`;
  - `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` — a DENY-ALL policy, defensible
    because the API returns only `application/json`/`application/problem+json` and NEVER renders HTML, so no
    script/style source needs allowing; `frame-ancestors 'none'` forbids framing and subsumes
    `X-Frame-Options`, so no separate framing header is added;
- the headers are applied from a `HttpResponse.OnStarting` callback registered early (right after
  `UseForwardedHeaders`, alongside the CORE-SEC-005 headers), so they survive the `Response.Clear()` the global
  exception and concurrency-conflict middleware perform on an error path — the baseline rides on a `500`/`409`
  Problem Details, not only the success path;
- the feature is ON BY DEFAULT (`SecurityHeaders:Enabled`) and each header is INDIVIDUALLY configurable: turning
  one header off (`SecurityHeaders:<Header>:Enabled=false`) removes exactly that header and leaves the others,
  and a blank configured value falls back to the documented default rather than emitting an empty header;
- every value is a STATIC directive string — never a tenant, principal, resource or request value — so the
  headers leak no tenant/principal detail (threat T7). Like the CORS allow-list, the rate limiter and transport
  security, they are a coarse browser-facing defense layered ON TOP OF the OIDC/tenant authorization every
  endpoint already enforces; they never widen authorization. This complements CORE-SEC-005.

## Required test categories

- foreign workspace ID denial
- hidden content denial
- reveal to selected participants only
- asset download denial
- realtime event recipient filter
- reconnect replay filter
- audit creation for visibility changes
