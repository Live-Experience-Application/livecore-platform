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
workspace_invitations(token_hash) unique
workspace_invitations(organization_id, workspace_id)
participants(workspace_id, id)
sessions(workspace_id, id)
scenes(workspace_id, id)
content_blocks(workspace_id, scene_id)
visibility_rules(session_id, resource_type, resource_id)
visibility_rules(session_id, resource_type, resource_id) unique where target_participant_id is null
visibility_rules(session_id, resource_type, resource_id, target_participant_id) unique where target_participant_id is not null
session_events(session_id, sequence) unique
session_events(session_id, created_at, event_id)
session_event_sequences(session_id)
assets(workspace_id, id)
asset_links(workspace_id, asset_id)
asset_links(workspace_id, asset_id, target_type, target_id) unique
audit_logs(organization_id, created_at)
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
idempotency_keys(scope, key)
```

## Optimistic concurrency (CORE-CONC-001)

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

The token is applied to exactly the aggregates that are updated in place:

```text
sessions
visibility_rules
workspaces
participants
quota_usage
purchase_transactions
```

Append-only tables (`session_events`, `audit_logs`, `purchase_events`,
`store_notification_events`) are never updated and so carry no token. The mapping is
PostgreSQL-only (the test suite's SQLite provider has no `xmin` system column), so it
is applied only when the provider is Npgsql.

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
`sequence` is contiguous with the prior entry's (detecting a deletion). It is tenant-scoped — a break in one
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

## JSONB use

JSONB is allowed for flexible template-defined attributes but not for core authorization fields.

Do not store visibility rules only inside arbitrary JSON.

## Migrations

Every schema change requires:

- migration file
- rollback strategy or forward fix plan
- database docs update
- tests if migration affects behavior
