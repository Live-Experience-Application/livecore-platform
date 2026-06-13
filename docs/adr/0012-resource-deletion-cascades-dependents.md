# ADR 0012: Resource Deletion Cascades Its Dependents

## Status

Accepted for initial implementation. First applied by CORE-LIFE-003 (entity deletion).

## Context

The "Resource Lifecycle and Deletion" epic introduces deletion of Core resources. A workspace
resource does not exist in isolation: other rows reference it. Those references come in two distinct
shapes, and they behave differently when the referenced resource is deleted:

- **Foreign-key references.** `entity_relationships.source_entity_id` and `target_entity_id` are real
  foreign keys into `entities(id)`, configured `ON DELETE CASCADE`. The database itself removes a
  dependent edge when its endpoint entity is deleted.
- **Polymorphic, non-foreign-key references.** `visibility_rules.resource_id` and
  `asset_links.target_id` name their target by a `(type, id)` pair across several resource tables, so a
  single column cannot be a foreign key (it would have to reference three / two different tables at
  once). The database therefore **cannot** cascade or restrict these: deleting the target leaves the
  rule or link behind, dangling.

A dangling polymorphic reference is not benign. A left-behind **visible** `visibility_rule` is a stale
audience-visibility grant that a later resource minted with the same id could silently inherit (a
visibility leak; threats T2/T5 in `docs/07_SECURITY_THREAT_MODEL.md`). A left-behind `asset_link` lets
an asset claim audience access through a target that no longer exists (threat T4). So a deletion story
must make a deliberate, documented choice between two policies:

- **Cascade** — delete the resource and clean up its dependents in the same operation.
- **Block** — refuse the deletion while any dependent still references the resource, and require the
  caller to remove the dependents first.

## Decision

**Deleting a Core resource CASCADES the cleanup of its dependents; it does not block on them.** The
cascade is performed in the **application layer**, inside a **single database transaction**, so the
resource, all of its dependents and the audit record commit together or roll back together:

1. Remove the **polymorphic, non-FK** dependents explicitly (the database cannot — `visibility_rules`
   for the resource, `asset_links` targeting the resource). This is the part the application *must* own.
2. Remove the **FK-backed** dependents explicitly too (`entity_relationships` touching the entity), even
   though the database FK would cascade them. Doing it in the application makes the cascade
   deterministic, observable, testable and identical across providers, and guarantees there is never an
   instant where a dangling edge exists; the database cascade then remains as defence in depth.
3. Delete the resource row.
4. Append an append-only audit record of the deletion (`AuditAction.EntityDeleted` for an entity).

The whole operation is **fail-closed and object-scoped**: the resource is loaded through its tenant- and
workspace-scoped repository lookup first, and every dependent removal is tenant- and workspace-scoped, so
a deletion can never reach across a workspace or tenant boundary (threats T1/T5).

### Why cascade, not block

A generic Core entity (and its rules, edges and links) is host-prepared content that lives entirely
inside one workspace; no other tenant holds a published contract against it. The host who deletes it
intends its dependents to go too. Blocking would strand an undeletable resource behind dependents the
same host must hunt down and remove one by one, with no safety benefit — the dependents are all in the
host's own workspace and become meaningless once the resource is gone. Cascading is the consistent,
least-surprising behavior and the only one that removes the dangling-reference hazard outright.

## Consequences

- All resource-deletion implementations follow this decision until superseded by a later ADR: cascade
  the dependents, in the application, inside one transaction, and audit the deletion.
- Application services that delete a resource own the cleanup of that resource's **polymorphic** (non-FK)
  references explicitly; they may rely on database FK cascades only as defence in depth, never as the
  sole mechanism.
- Deletion is a destructive, audited command authorized by the same host-capable roles
  (`Owner`/`Admin`/`Host`/`CoHost`) that create the resource (`docs/06_AUTHORIZATION_MATRIX.md`).
- This ADR does **not** introduce a soft-delete / tombstone model, a "deny deletion while referenced"
  (block) model, or cascade across tenants. A future requirement for any of those needs a new ADR and
  human approval.
- Any LLM-proposed change to this policy requires a new ADR and human approval.
