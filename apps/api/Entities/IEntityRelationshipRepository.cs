namespace LiveCore.Api.Entities;

/// <summary>
/// Persistence contract for the entity relationship aggregate (CORE-ENT-003). The Entities module
/// owns the <c>entity_relationships</c> table; other modules access relationships only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table
/// ownership violations; docs/05_MODULE_CONTRACTS.md: the Entities module owns "entity
/// relationships").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and
/// the workspace id, and a relationship is only ever returned when it belongs to exactly that
/// (organization, workspace) pair. The organization boundary is checked before the workspace
/// boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a relationship is never
/// returned through a foreign organization's id even when the workspace and ids are correct, and
/// never through a foreign workspace's id even when the organization and ids are correct. There is
/// deliberately no lookup of a relationship by id alone, no lookup that crosses tenants, and NO
/// list-everything method, so one workspace's edge can never be read through another workspace's id
/// and an edge in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// SAME-WORKSPACE-ENDPOINTS coupling: the by-source and by-entity listings filter by an entity id
/// within an explicit (organization, workspace) scope, so they never borrow another workspace's
/// edges. The relationship's own <c>source_entity_id</c> / <c>target_entity_id</c> foreign keys
/// guarantee the endpoints EXIST but not that they are in the relationship's workspace; ensuring an
/// edge is only ever created between two entities IN ITS OWN WORKSPACE is the responsibility of the
/// future create-relationship application flow (it resolves each endpoint through the
/// workspace-scoped <see cref="IEntityRepository.FindByIdAsync"/> before creating the relationship),
/// mirroring how the entity create flow resolves the type. This repository does not perform that
/// cross-aggregate check.
///
/// The aggregate is essentially immutable (its endpoints, kind, tenant and workspace never change),
/// so there is deliberately NO UpdateAsync: a relationship is created or deleted, never mutated.
///
/// CORE-ENT-003 was the aggregate + persistence story (no HTTP endpoint, no template loading or
/// schema-conformance validation — CORE-ENT-004 — and no search / visibility filtering / graph
/// traversal — CORE-ENT-005). CORE-LIFE-002 (the "Resource Lifecycle and Deletion" epic) later adds
/// the relationship REMOVAL contract (<see cref="RemoveAsync"/>) consumed by
/// <c>DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}</c>, so the edge
/// model is now shrinkable; template loading and search/visibility/graph traversal remain later
/// stories and are deliberately not built here. This contract takes explicit ids; resolving the
/// "current" organization or workspace from a request is the tenant context resolver's job and the
/// endpoint above does exactly that before calling in.
/// </summary>
public interface IEntityRelationshipRepository
{
    /// <summary>
    /// Finds the relationship with exactly the given id WITHIN the given organization and
    /// workspace, or <see langword="null"/> when no such relationship exists there. The
    /// organization and workspace both scope the lookup, so an edge that exists under another
    /// organization's or workspace's id is never returned, even when the surrogate id matches
    /// (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or relationship id is empty. An empty id can never address
    /// a stored relationship, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<EntityRelationship?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every relationship of the given workspace (owned by the given organization) in a
    /// deterministic order — sorted by the surrogate id, which is time-ordered (UUIDv7), so the
    /// sequence is stable and repeatable. The list is tenant- AND workspace-scoped: the predicate
    /// leads with <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign
    /// tenant's or a foreign workspace's edges are NEVER returned even when their ids would
    /// otherwise be addressable (threat T5/T1; the organization boundary is checked before the
    /// workspace boundary). An empty list is returned for a workspace with no relationships; the
    /// lookup never crosses the tenant or workspace boundary to borrow another workspace's edges.
    /// This is NOT a list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a stored
    /// workspace's relationships, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<EntityRelationship>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every relationship whose SOURCE is the given entity, WITHIN the given organization and
    /// workspace, in a deterministic order — sorted by the surrogate id (UUIDv7, time-ordered). The
    /// edge is directed, so this returns the OUTGOING edges of the entity only; the reverse
    /// (incoming) edges are not included (use <see cref="ListByEntityAsync"/> for both directions).
    /// The list is tenant-, workspace- AND source-scoped: the predicate leads with
    /// <c>organization_id</c>, then matches <c>workspace_id</c> and <c>source_entity_id</c>, so a
    /// foreign tenant's or workspace's edges are NEVER returned (threat T5/T1). An empty list is
    /// returned for an entity with no outgoing edges in the workspace. The source id is matched as a
    /// plain column value, so an edge whose endpoint lives in a DIFFERENT workspace (a
    /// same-workspace coupling the application layer is responsible for) would still be returned here
    /// for the workspace the edge is stored in.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or source entity id is empty. An empty id can never address
    /// a stored entity's edges, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<EntityRelationship>> ListBySourceEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every relationship that TOUCHES the given entity as its source OR its target, WITHIN
    /// the given organization and workspace, in a deterministic order — sorted by the surrogate id
    /// (UUIDv7, time-ordered). This is the undirected "all edges touching this entity" listing
    /// (both outgoing and incoming edges). The list is tenant- AND workspace-scoped: the predicate
    /// leads with <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign tenant's
    /// or workspace's edges are NEVER returned (threat T5/T1). An empty list is returned for an
    /// entity with no edges touching it in the workspace. This is a simple scoped list-by-entity, NOT
    /// a graph traversal (multi-hop traversal is CORE-ENT-005).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity id is empty. An empty id can never address a
    /// stored entity's edges, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<EntityRelationship>> ListByEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new relationship. Returns <see cref="EntityRelationshipAddResult.Added"/> on
    /// success or <see cref="EntityRelationshipAddResult.Duplicate"/> when the workspace already
    /// holds the same directed edge of the same kind (the per-workspace unique
    /// (<c>workspace_id</c>, <c>source_entity_id</c>, <c>target_entity_id</c>,
    /// <c>relationship_kind</c>) index rejected the insert). The existing row is left unchanged.
    /// Foreign-key violations (a non-existent source entity, target entity, workspace or tenant)
    /// surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<EntityRelationshipAddResult> AddAsync(
        EntityRelationship relationship,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES the given relationship row, removing the directed edge from the graph
    /// (CORE-LIFE-002). The edge model previously only ever grew — an edge could be added but never
    /// removed — so this is the inverse of <see cref="AddAsync"/>: it makes the graph shrinkable.
    /// The caller (the remove-relationship endpoint) is responsible for having loaded the
    /// relationship through the tenant- AND workspace-scoped <see cref="FindByIdAsync"/> first, so a
    /// foreign tenant's or foreign workspace's edge is never reachable to remove (threat T5/T1); this
    /// method removes exactly the row it is handed. Removal touches only this one edge row; the two
    /// endpoint entities are untouched (it is the reverse of the per-edge insert, not a cascade).
    /// </summary>
    Task RemoveAsync(
        EntityRelationship relationship,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES every edge that TOUCHES the given entity as its source OR its target, WITHIN the
    /// given organization and workspace, and returns the number removed (CORE-LIFE-003, the "Resource
    /// Lifecycle and Deletion" epic). This is the bulk inverse of <see cref="AddAsync"/> used when an
    /// entity is deleted: every directed edge the entity is an endpoint of must go (an edge has no
    /// meaning once one of its endpoints is gone), so this is the application-level CASCADE the
    /// <see cref="EntityDeletionService"/> applies before deleting the entity row
    /// (docs/adr/0012-resource-deletion-cascades-dependents.md). The database endpoint foreign keys are
    /// already <c>ON DELETE CASCADE</c>, so this is defence in depth that makes the cascade explicit,
    /// observable and identical across providers; doing it BEFORE the entity delete also means there is
    /// never a window where a dangling edge exists. The deletion is tenant- AND workspace-scoped (the
    /// predicate leads with <c>organization_id</c> then <c>workspace_id</c>), so a foreign tenant's or
    /// workspace's edges are NEVER removed even when the entity id would otherwise be addressable
    /// (threat T5/T1). Removing the edges of an entity with no edges is a no-op that returns 0.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity id is empty. An empty id can never address a stored
    /// entity's edges, so the deletion is rejected instead of silently affecting nothing.
    /// </exception>
    Task<int> RemoveByEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityId,
        CancellationToken cancellationToken);
}
