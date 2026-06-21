# Database Schema

Use PostgreSQL.

## Principles

- tenant-scoped tables include `organization_id`
- workspace-scoped tables include `workspace_id`
- session-scoped tables include `session_id`
- use UUID or ULID-style IDs consistently
- use timestamptz for timestamps
- use optimistic concurrency where needed
- session events are append-only
- audit logs are append-only
- avoid hard-delete for business data; use soft delete where needed
- high-value domain invariants are enforced as `CHECK` constraints, not only in aggregate guards (see
  "Domain-invariant CHECK constraints")

## Core tables

The source of truth for the Core schema is `csv/database_tables.csv` (one row
per table, with its owning module, scope and notes). The list below mirrors
that file and the implemented EF Core model; keep all three in step (see
`docs/24_SPEC_CONSISTENCY.md`). Tables documented elsewhere but not present in
this list are not in the implemented schema (for example `purchase_providers`,
whose provider handling is in-code rather than a table; the
`billing_account_links` buyer-linkage table is now implemented by CORE-MON-002 —
after CORE-MON-001 reversed the CORE-DOC-002 post-v1 deferral — and so appears in
the list below; see `csv/entitlement_database_tables.csv` and
`docs/24_SPEC_CONSISTENCY.md`).

Current Core tables:

```text
organizations
users
push_subscriptions
organization_members
workspaces
workspace_members
workspace_invitations
participants
sessions
scenes
content_blocks
entities
entity_types
entity_relationships
assets
asset_links
visibility_rules
session_events
session_event_sequences
push_notification_deliveries
audit_logs
audit_log_sequences
templates
export_jobs
export_manifests
export_manifest_entries
recaps
entitlement_definitions
plan_definitions
plan_entitlements
subject_entitlements
quota_definitions
quota_usage
purchase_transactions
purchase_events
store_notification_events
billing_account_links
idempotency_keys
```

## Critical indexes

Examples:

```text
organizations(id)
workspaces(organization_id, id)
workspace_members(workspace_id, user_id)
push_subscriptions(user_id, endpoint) unique
workspace_invitations(token_hash) unique
workspace_invitations(organization_id, workspace_id)
participants(workspace_id, id)
sessions(workspace_id, id)
scenes(workspace_id, id)
content_blocks(workspace_id, scene_id)
visibility_rules(session_id, resource_type, resource_id)
visibility_rules(session_id, resource_type, resource_id) unique where target_participant_id is null
visibility_rules(session_id, resource_type, resource_id, target_participant_id) unique where target_participant_id is not null
visibility_rules(workspace_id, resource_type, resource_id)
visibility_rules(scheduled_reveal_at) where scheduled_reveal_at is not null
session_events(session_id, sequence) unique
session_events(session_id, created_at, event_id)
session_event_sequences(session_id)
assets(workspace_id, id)
asset_links(workspace_id, asset_id)
asset_links(workspace_id, asset_id, target_type, target_id) unique
audit_logs(organization_id, created_at)
audit_logs(organization_id, id)
audit_logs(organization_id, sequence) unique
audit_log_sequences(organization_id)
export_jobs(workspace_id, id)
export_manifests(workspace_id, id)
export_manifests(export_job_id) unique
export_manifest_entries(export_manifest_id, kind) unique
recaps(workspace_id, id)
recaps(session_id, id)
recaps(session_id) unique where generated_by is null
entitlement_definitions(key) unique
plan_definitions(key) unique
plan_entitlements(plan_definition_id, entitlement_definition_id) unique
purchase_transactions(provider, provider_transaction_id) unique
purchase_events(purchase_transaction_id, created_at)
billing_account_links(purchase_transaction_id) unique
billing_account_links(subject_type, subject_id)
idempotency_keys(scope, key)
```

### Paged audit-log read index `audit_logs(organization_id, id)` (CORE-PERF-007)

The view-audit-log page read (`AuditLogRepository.ListPageByOrganizationAsync`, reached live by
`GET /api/v1/audit-logs`) and the full id-ordered read (`ListByOrganizationAsync`) are
`WHERE organization_id = X ORDER BY id` (the time-ordered UUIDv7 surrogate) with `OFFSET`/`LIMIT`. The
`(organization_id, created_at)` critical index and the unique `(organization_id, sequence)` index both lead
with the tenant but order by a **different** column, so neither satisfies the id ordering — without a
matching composite PostgreSQL matches the tenant prefix and then **sorts it on every page**, a cost that
grows without bound as the append-only log grows. The non-unique `audit_logs(organization_id, id)` covering
index closes that gap: the tenant equality fixes `organization_id` and the index already yields rows in `id`
order, so a page is index-backed with **no full sort, independent of offset depth**. It is the
organization-scoped analogue of the `(workspace_id, id)` index every workspace-scoped id-ordered list carries
(`assets`, `export_jobs`, `recaps`). It is purely an ordering aid (the id is already unique on its own), and
the `(organization_id, created_at)` index still backs future time-range audit queries (CORE-AUD-005) while
the unique `(organization_id, sequence)` index still backs the append-order hash-chain walk (CORE-SEC-003 /
CORE-PERF-005).

## Optimistic concurrency (CORE-CONC-001, CORE-CONC-006, CORE-WS-006)

The mutable aggregates carry an optimistic-concurrency token so a concurrent
read-modify-write fails loudly instead of silently losing an update. The token is the
PostgreSQL system column `xmin` (the id of the transaction that last wrote the row),
mapped as an EF Core row-version concurrency token
(`Property<uint>("xmin").IsRowVersion().HasColumnName("xmin")`). Because `xmin` is a
column every row already has, this needs **no data migration and adds no real column**
— the schema is unchanged; only the EF model maps it. PostgreSQL advances `xmin` on
every UPDATE, so EF adds `WHERE ... AND xmin = @original` to a write and a stale write
affects zero rows, raising a conflict that the API surfaces as `409`
(`docs/08_API_CONTRACTS.md`).

The token is applied to exactly the aggregates that are updated in place. CORE-CONC-001
mapped the first six; CORE-CONC-006 extended it to the remaining eight that were still
doing a bare `Update`/`SaveChanges` with no token (and so silently lost a concurrent
update under last-write-wins); CORE-WS-006 added `workspace_invitations`, which becomes
in-place-updated when an invitation is redeemed (`Pending -> Accepted`), so two
concurrent redemptions of one single-use token cannot both grant a membership:

```text
sessions
visibility_rules
workspaces
participants
quota_usage
purchase_transactions
content_blocks
entities
entity_types
scenes
assets
subject_entitlements
export_jobs
users
workspace_invitations
```

Append-only tables (`session_events`, `audit_logs`, `purchase_events`,
`store_notification_events`) are never updated and so carry no token. The mapping is
PostgreSQL-only (the test suite's SQLite provider has no `xmin` system column), so it
is applied only when the provider is Npgsql.

## Data-subject erasure and the user foreign keys (CORE-PRIV-001)

The `users` table is **global** scope (it references identities, not tenant data, so it carries no
`organization_id`). It holds the data subject's PII: the OIDC `issuer`/`subject_id`, `email` and
`display_name`. The right to erasure (GDPR Art.17) **hard-deletes** the `users` row — an explicit exception to
the "avoid hard-delete for business data" principle, because the row itself is the personal data — and the
schema's foreign keys into `users(id)` are designed so the deletion cascades correctly without stranding or
losing dependent records:

- `assets.created_by`, `export_jobs.requested_by` and `participants.user_id` are nullable **`ON DELETE SET
  NULL`**: the dependent record SURVIVES with an anonymized (null) creator/requester/user link;
- `organization_members.user_id` and `workspace_members.user_id` are **`ON DELETE CASCADE`**: the subject's
  access grants are revoked everywhere; `push_subscriptions.user_id` (CORE-PUSH-001) is likewise **`ON DELETE
  CASCADE`**, so the subject's per-principal Web Push subscriptions are removed with their profile;
- `audit_logs` reference the actor/resource as **recorded facts, not foreign keys**, so the PII-free
  append-only audit trail and its per-tenant hash chain survive a user deletion intact (the erasure is
  reconcilable with the immutable audit log — `docs/07_SECURITY_THREAT_MODEL.md`).

Two PII columns are NOT reachable by any user foreign key — `participants.display_name` and
`workspace_invitations.invited_email` — so the erasure command anonymizes them explicitly (to fixed,
non-identifying placeholders) before deleting the profile, in one transaction. No schema change is required:
the erasure is the application command the existing `SET NULL`/`CASCADE` foreign keys already assumed.

## Authorized tenant organization deletion and the tenant cascade (CORE-PRIV-002)

`organizations` is the tenant root: every tenant-scoped table carries an `organization_id` that is a foreign
key into `organizations(id)`, and **every one of those foreign keys is `ON DELETE CASCADE`** (workspaces,
workspace members and invitations, organization members, sessions, scenes, content blocks, entities, entity
types, entity relationships, participants, visibility rules, assets, asset links, export jobs, export manifests,
recaps, session events, templates, and the `audit_logs` / `audit_log_sequences` tables). So deleting one
`organizations` row tears the **whole tenant** down in a single operation — the right to tenant offboarding /
data deletion — without any application-level child enumeration. The `audit_logs` tenant foreign key cascades
too: the tenant's own audit log is **intentionally** part of the teardown, so an offboarded tenant leaves no
tenant-scoped data behind. Like the erasure above, this is an explicit exception to the "avoid hard-delete for
business data" principle — the story IS the deletion of the tenant and all its data.

No schema change is required: the deletion command (`DELETE /api/v1/organizations/{organizationSlug}`, Owner
only) is the application command the existing `ON DELETE CASCADE` foreign keys already assumed but nothing
previously exercised. Because the deleted tenant's own audit log is cascade-removed, the offboarding itself is
recorded as a **platform-level** `audit_logs` row (`organization_id IS NULL`, **outside** the per-tenant hash
chain — the same posture as the entitlement/store facts) so the security record SURVIVES the teardown; the
deleted organization id is carried as the audit row's `resource_id` (a recorded fact, not a tenant foreign key,
so it is not cascade-removed). The audit append and the cascade delete commit in **one transaction**.

Global and subject-keyed tables carry no `organization_id` foreign key, so the cascade never reaches them
(`users` is a global identity; `purchase_transactions`/`purchase_events`/`store_notification_events` are
deployment-spanning; `subject_entitlements`/`quota_usage` are keyed by a polymorphic subject pair with no
organization foreign key). An organization-subject entitlement/quota row therefore is not removed by the
tenant cascade — it becomes unreachable residue rather than a dangling foreign key.

## Configurable data-retention sweeps (CORE-PRIV-003)

Until this story Core kept terminal/old personal-data-bearing records **forever**: a completed/cancelled
session and its append-only `session_events`, a generated `recaps` row, a completed `export_jobs` row and its
manifest, and a closed/expired/revoked `workspace_invitations` row (its plaintext `invited_email`) all lived for
the deployment's lifetime. GDPR Art.5(1)(e) (storage limitation) wants those expired once they are no longer
needed. The data-retention sweep — a worker loop (`apps/worker`) that reuses the asset-cleanup pattern — does
that on configurable, **per-family** windows. No new table is added; the windows are deployment policy
(`Retention:*`, see `docs/13_SELF_HOSTING_REQUIREMENTS.md`), not schema.

The retention window of each family is measured from the record's **creation/generation time** (its age), which
is also what its time-ordered UUIDv7 surrogate id encodes — so the sweep lists candidates ordered by id,
bounded by a batch size, and applies the age threshold after materialization (the SQLite test provider cannot
compare a `timestamptz`, exactly as the asset-cleanup sweep notes), which keeps the sweep provider-portable and
free of starvation. The families and what a purge removes:

- **Completed/expired sessions** (status `Ended` or `Cancelled`). Deleting the `sessions` row triggers the
  existing `ON DELETE CASCADE` foreign keys into `sessions(id)`, so its `session_events` (and their
  `session_event_sequences` counter), its `recaps` and its session-scoped `visibility_rules` are removed with
  it — the "completed/expired sessions and their session events" purge. This is a deliberate exception to the
  "NEVER delete append-only `session_events`" rule the session **cancel** command observed: cancel is a
  lifecycle off-ramp that preserves history, whereas the retention sweep is the storage-limitation expiry of
  history that is past its window.
- **Generated recaps** (`recaps`). The one removal path on the otherwise write-once recap; the recap body is
  host content (potentially personal data). Independently windowed, so a recap may be expired before (or kept
  longer than) its session.
- **Completed export artifacts** (`export_jobs` with status `Completed`). The new nullable
  `export_jobs.artifact_bucket` / `export_jobs.artifact_object_key` columns record WHERE a completed export's
  produced object-storage blob lives (Core's manifest-only export pipeline writes no blob and leaves them
  `NULL`); the sweep deletes that **object first, then the row** (the `export_manifests.export_job_id` foreign
  key cascades the manifest away), so a purged export never leaves an orphaned object. With storage unconfigured
  the row is kept (fail-closed; threat T4).
- **Closed/expired/revoked invitations** (`workspace_invitations` that are `Accepted`, `Revoked`, or `Pending`
  but past `expires_at`). The purge removes the plaintext `invited_email`; the invitation's lifecycle audit
  facts are separate `audit_logs` rows that survive.
- **Idempotency keys** (`idempotency_keys`, CORE-PRIV-006). The table is **insert-only** retry-safety
  infrastructure (`SystemModule/IdempotencyKeyStore.cs`) — a row is written on every idempotent
  create/reveal/purchase replay and was never reclaimed, so it grew **unbounded** over a deployment's lifetime.
  This family deletes rows by **age alone** once they are well past any plausible client retry horizon (a
  30-day default window, **enabled** by default). It is the one **non-tenant-scoped** family: `idempotency_keys`
  has no `organization_id` and the rows carry no host content (only a server-composed `scope` partition and a
  client `key` correlation token), so the purge is **not** audited per row and is logged **by count, never by
  key value**. It is a bounded bulk delete (oldest `id` first, batch-limited), so it is naturally idempotent and
  concurrency-safe — a row another sweep already removed simply matches nothing. No schema change is needed: the
  existing `idempotency_keys(id)` primary key orders the sweep and the `created_at` column gives the age.

Every **tenant-scoped** purge (the first four families) is **audited by id**: a tenant-scoped `audit_logs`
`RecordRetentionPurged` fact records the tenant, workspace and the purged record's generic kind name + surrogate
id, with **no actor** (a system job) and no content (threat T7). Because the audit reference is a recorded fact,
not a foreign key, the audit row survives the purge and the per-tenant tamper-evident hash chain still verifies.
Each such purge (audit append + delete) commits in **one transaction** and re-loads its record tenant-scoped
inside that transaction, so overlapping sweeps (or worker replicas) are idempotent and concurrency-safe — a
record already purged is skipped, and a lost delete race rolls the audit append back with it.

## Session-scoped visibility rules (CORE-SVIS-001)

`visibility_rules` is **session-scoped**: it carries a required `session_id` column (a foreign key into
`sessions(id)`, `ON DELETE CASCADE`) in addition to `organization_id` and `workspace_id`. A reveal is
session-scoped (`docs/adr/0013-session-scoped-visibility-rules.md`): a workspace may run several
**concurrent** sessions, and a resource revealed in one session must be visible **only** within that
session — a participant connected to a different concurrent session of the same workspace must never see
it (the cross-session leak; threats T5/T3 in `docs/07_SECURITY_THREAT_MODEL.md`). The critical index is
therefore led by the session: `visibility_rules(session_id, resource_type, resource_id)`. Every
session-scoped visibility surface (the reveal/hide command, the participant-visible feed, the realtime
recipient gate and reconnect replay) is bounded by `session_id`; the role-level, session-agnostic
asset-download and entity-search reads remain workspace-wide. The lead index is **non-unique** because it
spans both dimensions (a resource carries at most the audience-wide rule plus one rule per selected
participant within a session); it backs the "all rules for this resource in this session" read.

A second, workspace-wide read survives: the **resource-deletion cleanup** (`RemoveByResourceAsync`) removes
**all** of a deleted resource's rules — the audience-wide rule and every selected-participant rule, **across
the workspace's sessions** — because `resource_id` is a polymorphic reference the database cannot cascade
(`docs/adr/0012-resource-deletion-cascades-dependents.md`). That delete is session-**agnostic**, so it is
backed by the workspace-led cleanup index `visibility_rules(workspace_id, resource_type, resource_id)`
(CORE-PERF-004). CORE-SVIS-001 re-led the resource read index with `session_id` and dropped the old
workspace-led composite, which left the cleanup predicate uncovered (only the single-column workspace
foreign-key index); CORE-PERF-004 restores it, and because `workspace_id` is its prefix the composite also
serves the workspace foreign key (the separate single-column workspace index is therefore retired).

`visibility_rules` additionally carries a **`locked`** boolean column (CORE-VSEAL-001, `NOT NULL` default
`false`): the **sealed/locked** authoring flag that makes the governed resource permanently-restricted. While a
rule is locked, a reveal/hide/visibility-change targeting it is refused **fail-closed with `409`**. It is an
**orthogonal** authoring flag, **not** a third value of the `visibility` column — a first-class boolean column
(authorization-relevant fields are real columns, never inside arbitrary JSON), so it reshapes **no** index and
leaves the binary Hidden/Visible enforcement and the recipient resolver exactly as before. The default of
`false` means every pre-existing row is unlocked, so an unlocked rule behaves exactly as before. The flag is
set/cleared only by the authoring roles (the lock/unlock commands) and is projected on `VisibilityRuleResponse`
and, where audience-safe, on the participant visible-feed item.

`visibility_rules` also carries an optional **`scheduled_reveal_at`** `timestamptz` column (CORE-VSEAL-002,
**nullable**, no default): the time at which a **Hidden** rule is to be **automatically revealed** by the worker's
background sweep, which drives the **same central reveal command** as a live host reveal. It is a first-class
server-fact column (never inside arbitrary JSON), **orthogonal** to the `visibility` column; `null` (the default)
means no schedule, so a rule without it behaves exactly as before. A **filtered (partial)** index
`visibility_rules(scheduled_reveal_at)` **where `scheduled_reveal_at IS NOT NULL`** backs the worker's periodic
due-rule sweep cheaply (the vast majority of rules carry no schedule, so the partial index stays small) — the
same partial-index technique the dimension-uniqueness indexes use. The sweep is **idempotent** (an auto-revealed
rule is no longer Hidden, plus a deterministic per-rule reveal idempotency key) and **tenant-safe** (each
auto-reveal is driven scoped to the rule's own tenant/workspace/session); the column is projected on
`VisibilityRuleResponse` and, where audience-safe, on the participant visible-feed item.

## Single rule per dimension (CORE-SVIS-002)

A resource has **at most one active visibility rule per `(session, resource, dimension)`**. The dimension is
either audience-wide (`target_participant_id IS NULL`) or one selected participant
(`target_participant_id IS NOT NULL`). Before this, two concurrent first-reveals of one resource each
inserted a visible rule and a later hide flipped only one, leaving the other Visible as an **un-hideable
ghost reveal** (threats T5/T3 in `docs/07_SECURITY_THREAT_MODEL.md`).

Because `target_participant_id` is nullable and both PostgreSQL and SQLite treat NULLs as **distinct** in a
unique index, a single unique index over the four columns would not reject a second audience-wide rule (two
NULL targets compare distinct). The constraint is therefore expressed as **two filtered (partial) unique
indexes** — the same nullable-uniqueness pattern as `templates(organization_id IS NULL / IS NOT NULL)`:

- `visibility_rules(session_id, resource_type, resource_id)` **unique** where `target_participant_id IS NULL`
  — at most one audience-wide rule.
- `visibility_rules(session_id, resource_type, resource_id, target_participant_id)` **unique** where
  `target_participant_id IS NOT NULL` — at most one rule per selected participant (reveals to **different**
  participants stay independent dimensions).

The reveal command relies on these via **insert-on-conflict**: a first-create that loses the race against a
concurrent first-reveal is reported as a duplicate and converges onto the one rule rather than creating a
second, so concurrent first-reveals never produce two rules and a hide always fully reverses a reveal.

## Per-session event sequence (CORE-RTC-001)

Every `session_events` row carries a `sequence` column: a **per-session, gap-free, strictly monotonic**
number that is the authoritative ordering and replay key for the session stream. The stream is read and
replayed by `session_events(session_id, sequence)` (a **unique** index) rather than by the UUIDv7
`event_id`, which is only monotonic at **millisecond** resolution and so reorders events appended within one
millisecond — a single reveal publishes `ContentRevealed`, `VisibilityRuleChanged` and (for a scene)
`SceneActivated` at the same instant, whose order would otherwise be undefined. Ordering by the sequence
preserves their append order, and a client detects a missed event as a **gap** in the sequence.

The numbers are handed out by a `session_event_sequences` counter table — one row per session,
`session_event_sequences(session_id)` the primary key and a `sessions(id)` foreign key that **CASCADES** on
delete (the counter is removed with its session, like the stream it feeds), plus a `last_sequence` column.
The append path allocates the next number with a single atomic `INSERT ... ON CONFLICT (session_id) DO
UPDATE SET last_sequence = last_sequence + 1`: the conflict-path UPDATE takes a row lock that **serializes**
concurrent appends to the same session (the second blocks and then increments from the committed value
rather than colliding), so the sequence stays gap-free and strictly monotonic even under a race. Because the
increment runs in the command's unit-of-work transaction (CORE-CONC-002) together with the event insert, a
rollback reclaims the number — there is no gap. The unique `(session_id, sequence)` index is the integrity
backstop that guarantees no two events of a session ever share a sequence.

## One system recap per session (CORE-RCP-001)

A session has **at most one SYSTEM recap** regardless of how many worker replicas or overlapping sweeps run.
The background recap generation job decides eligibility with a `NOT EXISTS` read (ended sessions with no
recap) that is **decoupled** from the bare insert, has no single-instance guard, and mints a **fresh UUIDv7**
primary key per recap — so before this, two concurrent sweeps both read the session as eligible and both
inserted, leaving the session with **duplicate** system recaps (the other `recaps` indexes are non-unique).

Because `generated_by` is nullable and a recap may be produced by the **system** (`generated_by IS NULL`) or by
a **host** (`generated_by IS NOT NULL`, of which a session may legitimately have many), the at-most-one rule
applies only to system recaps. It is enforced by a single **filtered (partial) unique index** — the same
nullable-uniqueness pattern as the visibility per-dimension rule (CORE-SVIS-002) and
`templates(organization_id)`:

- `recaps(session_id)` **unique** where `generated_by IS NULL` — at most one system recap per session; host
  recaps stay unconstrained.

The generation job relies on this via **insert-on-conflict** (`RecapRepository.TryAppendSystemRecapAsync`): a
losing concurrent append is rejected by the index and converges onto the recap that already exists — reported
as a deduplicated no-op, never a duplicate and never a failure — so "a second concurrent sweep produces no
duplicate" and the worker loop needs **no single-instance guard**. A genuine persistence error (the
session/workspace/tenant was deleted between the eligibility read and the append) still surfaces, so the sweep
leaves that session eligible and retries it. The filter SQL is portable across PostgreSQL and SQLite, so the
test schema and the migration build it identically.

## Buyer linkage for verified purchases (CORE-MON-002)

`purchase_transactions` carries **no buyer column** on purpose (CORE-STORE-002): a purchase is named
**globally** by its `(provider, provider_transaction_id)` pair, so two users who submit the same external
receipt collapse to one row, and the authenticated buyer was verified then discarded. `billing_account_links`
is the missing link that records **which subject** a verified purchase belongs to, so a verified purchase can
later grant that subject the mapped entitlement (CORE-MON-003).

A row binds one `purchase_transaction_id` (a `purchase_transactions(id)` foreign key, **CASCADE** on delete —
the link is part of the purchase's lifecycle, like `purchase_events`) to one buyer subject
(`subject_type`, `subject_id`). The subject pair is the **same shape** `subject_entitlements` uses: a store
purchase is made by a person, so the buyer is a `User` subject whose id is the buyer's `users(id)` profile id,
and `subject_id` is a **polymorphic** reference with no database foreign key (mirrors
`subject_entitlements.subject_id`), so isolation is purely by the subject pair. `subject_type` is persisted by
its stable enum **name** (a real string column, never JSON — core authorization state).

The **one-subject-per-receipt** guarantee is the **unique** index
`billing_account_links(purchase_transaction_id)`: a verified purchase can be linked to only one subject, so
once user A's receipt is linked to A, user B can never bind the same receipt to B — the second claim is
rejected at the database and denied fail-closed. The link is **immutable** (which subject a purchase belongs to
never changes; re-binding to a different subject is exactly what the uniqueness rule forbids), so like the
append-only tables it carries no optimistic-concurrency token. The Apple and Google verification endpoints
record the purchase and link the buyer in **one transaction** (CORE-CONC-002), so the buyer linkage is durably
atomic with the recording.

A second, **non-unique** index `billing_account_links(subject_type, subject_id)` serves the reverse,
subject-scoped read (CORE-MON-012): the narrow cross-product entitlement-revocation check lists **all** of one
subject's linked purchases to decide whether a refunded purchase's entitlement is still granted by another of the
subject's active purchases (and so must be retained). It mirrors the `subject_entitlements(subject_type,
subject_id)` lookup prefix and is independent of the one-subject-per-receipt unique index above.

## Tamper-evident audit log (CORE-SEC-003)

`audit_logs` is **tamper-evident**: each entry is cryptographically chained to the previous entry of the same
tenant, so altering, deleting or reordering a persisted row is **detectable**. The log was already
application-level append-only (an immutable aggregate, an append + read repository with no update/delete path),
but nothing detected a **DB-level** actor or a future regression that wrote to the table directly. The hash
chain closes that gap, layered **under** the deployment step that REVOKEs `UPDATE`/`DELETE` on `audit_logs`
(`docs/13_SELF_HOSTING_REQUIREMENTS.md`): the REVOKE prevents tampering, the chain detects it if the database
role is ever bypassed or misconfigured.

Each `audit_logs` row carries three columns added for this:

- `sequence` — a per-tenant, **gap-free, strictly monotonic APPEND** number; the spine the chain is linked
  along. It is distinct from the surrogate `id`, which is time-ordered by the **event** time and may be recorded
  out of append order; the chain follows append order, so it follows the sequence.
- `previous_hash` — the `entry_hash` of the entry that precedes this one in the tenant's chain; `NULL` only for
  the tenant's genesis entry (and for legacy rows, below).
- `entry_hash` — a **SHA-256** over the entry's recorded fields (every column, the `sequence` and the
  `previous_hash`), so altering any field changes it. Nullable only so **legacy** rows written before this
  hardening (whose hash cannot be computed in portable migration SQL) round-trip; every entry appended through
  the sealed append path carries a non-null hash, and the verifier treats hash-less rows as unverified legacy.

The numbers are handed out by an `audit_log_sequences` counter table — one row per tenant,
`audit_log_sequences(organization_id)` the primary key with an `organizations(id)` foreign key that **CASCADES**
on delete (the counter is removed with the tenant, like the audit log it sequences), plus a `last_sequence`
column. The append path allocates the next number with a single atomic
`INSERT ... ON CONFLICT (organization_id) DO UPDATE SET last_sequence = last_sequence + 1`: the conflict-path
UPDATE takes a row lock that **serializes** concurrent appends to the same tenant, so the sequence stays
gap-free and the chain never **forks** even under a race. Because the increment runs in the command's
unit-of-work transaction (CORE-CONC-002) together with the audit insert, a rollback reclaims the number — there
is no gap. This is the audit analogue of the per-session event sequence (CORE-RTC-001), scoped to the tenant.
The unique `audit_logs(organization_id, sequence)` index is the integrity backstop guaranteeing no two entries
of a tenant ever share a sequence, so the chain stays a single linear spine.

The **verification routine** (`AuditLogChainVerifier`) reads a tenant's entries in append (sequence) order and
checks, for every chained entry: its stored `entry_hash` recomputes from its content (detecting an edit), its
`previous_hash` links to the prior entry's `entry_hash` (detecting an insertion/removal/reorder), and its
`sequence` is contiguous with the prior entry's (detecting a deletion). It STREAMS the chain in bounded ordered
segments via the `(organization_id, sequence)` cursor (`WHERE organization_id = X [AND sequence > cursor]
ORDER BY sequence LIMIT n`, backed by the unique `audit_logs(organization_id, sequence)` index) rather than
materializing a tenant's whole chain in memory, so verification memory/time stay bounded as the log grows
(CORE-PERF-005); detection is unchanged and stops at the first broken entry. It is tenant-scoped — a break in one
tenant never implicates another — and content-free.

The chain is an **unsigned** SHA-256 chain (no secret key), so it detects accidental corruption, an isolated
row edit/deletion and a bypass of the append path; a privileged actor with full write access who knows the
algorithm could in principle rewrite the whole chain, which is what the REVOKE deployment step (and, beyond
Core, external anchoring/signing — a documented follow-up) defends against. The read contract is unchanged:
the existing append + tenant-scoped reads (CORE-SEC-002) keep their shape; verification is a separate routine.

### Platform-level (tenant-less) audit facts (CORE-SPEC-002)

`audit_logs.organization_id` is **nullable** (ADR 0014). A **null** organization marks a PLATFORM-LEVEL audit
fact — a deployment-spanning, not tenant-scoped security event such as a purchase grant/revocation, a purchase
verification or a store notification, which carries no organization (a purchase is named globally and a user's
premium follows the user, not a tenant — `docs/21`). The tenant foreign key stays, now **optional**, so a set
organization is still enforced at the row level (threat T5); a platform fact has no tenant key. A platform fact
is append-only but stands **outside** the per-tenant hash chain above (whose spine is the per-tenant append
sequence): it is appended unsequenced (`sequence` 0, null hashes), exactly the append-only posture the
`purchase_events` trail has, and the per-tenant chain and `audit_log_sequences` are unchanged. The tenant-scoped
reads filter by a concrete `organization_id`, so a platform fact is never returned through any tenant's id
(threat T5). The unique `audit_logs(organization_id, sequence)` index is unchanged.

## Domain-invariant CHECK constraints (CORE-CONC-009)

The high-value invariants the aggregates enforce in memory are **also** enforced at the database as additive
`CHECK` constraints, so a code-impossible state cannot be persisted by a path that bypasses the aggregate — a
direct DB write (the migration/owner role legitimately keeps `DELETE` for tenant teardown), a future raw-SQL
path or a mapping regression. This is the **defence-in-depth** sibling of the audit log's tamper resistance
(CORE-SEC-004): the aggregate guards remain the primary enforcement, and the constraints are the backstop that
makes the invariant true of the data, not only of the code that normally writes it.

The constraints are **additive and behaviour-neutral** (expand-only): every value the aggregates produce
already satisfies them, so valid writes are unaffected and only an out-of-range value is rejected at the
database. The migration that adds them (`AddDomainInvariantCheckConstraints`) only `ADD`s constraints; its
`Down()` only `DROP`s them (no `DropColumn`/`DropTable`), so it loses no row data and is **not** a destructive
`Down()` — the destructive-down review (`csv/migration_destructive_down_review.csv`) is unaffected.

- **status-enum ranges** — `workspaces`, `workspace_invitations`, `sessions`, `participants`, `export_jobs`
  and `assets` each persist their lifecycle status by its stable enum **name** (a real string column), so the
  constraint allowlists exactly that enum's defined names (e.g. `sessions.status IN ('Prepared', 'Live',
  'Ended', 'Cancelled')`).
- **`scenes.scene_order >= 0`** — the non-negative ordering position.
- **`content_blocks.revision_number >= 1`** — the monotonic revision counter starts at 1.
- **`quota_usage.used_amount >= 0`** — recorded usage never goes negative.
- **`assets.size_bytes`** is `NULL` (while the asset is pending) **or** `>= 0` — the byte counter.
- **`session_events.sequence >= 1`** — the per-session position is strictly positive (the append path stamps a
  real position before the row is saved; the unassigned `0` a fresh event carries never reaches the table).

These deliberately do **not** add the intentionally-absent polymorphic foreign keys (the `CORE-SPEC-003`
register; e.g. `visibility_rules.resource_id`, `session_events.visibility_subject_id`), which stay non-FK
references by design.

## JSONB use

JSONB is allowed for flexible template-defined attributes but not for core authorization fields.

Do not store visibility rules only inside arbitrary JSON.

## Migrations

Every schema change requires:

- migration file
- rollback strategy or forward fix plan
- database docs update
- tests if migration affects behavior
