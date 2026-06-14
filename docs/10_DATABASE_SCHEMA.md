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
this list are not in the implemented schema and are tracked as deferred (for
example `purchase_providers` and `billing_account_links`, which CORE-DOC-002
formally defers to post-v1; see `csv/entitlement_database_tables.csv` and
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
audit_logs
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
session_events(session_id, created_at, event_id)
assets(workspace_id, id)
asset_links(workspace_id, asset_id)
asset_links(workspace_id, asset_id, target_type, target_id) unique
audit_logs(organization_id, created_at)
export_jobs(workspace_id, id)
export_manifests(workspace_id, id)
export_manifests(export_job_id) unique
export_manifest_entries(export_manifest_id, kind) unique
recaps(workspace_id, id)
recaps(session_id, id)
entitlement_definitions(key) unique
plan_definitions(key) unique
plan_entitlements(plan_definition_id, entitlement_definition_id) unique
purchase_transactions(provider, provider_transaction_id) unique
purchase_events(purchase_transaction_id, created_at)
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

## JSONB use

JSONB is allowed for flexible template-defined attributes but not for core authorization fields.

Do not store visibility rules only inside arbitrary JSON.

## Migrations

Every schema change requires:

- migration file
- rollback strategy or forward fix plan
- database docs update
- tests if migration affects behavior
