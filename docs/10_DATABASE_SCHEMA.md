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
visibility_rules(workspace_id, resource_type, resource_id)
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

## JSONB use

JSONB is allowed for flexible template-defined attributes but not for core authorization fields.

Do not store visibility rules only inside arbitrary JSON.

## Migrations

Every schema change requires:

- migration file
- rollback strategy or forward fix plan
- database docs update
- tests if migration affects behavior
