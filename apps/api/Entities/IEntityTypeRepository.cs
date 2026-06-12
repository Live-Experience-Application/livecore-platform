namespace LiveCore.Api.Entities;

/// <summary>
/// Persistence contract for the entity type aggregate (CORE-ENT-001). The Entities module
/// owns the <c>entity_types</c> table; other modules access entity types only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table
/// ownership violations; docs/05_MODULE_CONTRACTS.md: the Entities module owns "entity
/// types").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization
/// id and the workspace id, and an entity type is only ever returned when it belongs to
/// exactly that (organization, workspace) pair. The organization boundary is checked before
/// the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so an
/// entity type is never returned through a foreign organization's id even when the workspace
/// and ids are correct, and never through a foreign workspace's id even when the organization
/// and ids are correct. There is deliberately no lookup of an entity type by id alone, no
/// lookup that crosses tenants, and NO list-everything method, so one workspace's entity type
/// can never be read through another workspace's id and a type in one tenant can never be
/// read through another tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1
/// broken object-level authorization).
///
/// This is the aggregate + persistence story. There are NO HTTP endpoints (csv/api_routes.csv
/// defines no entity-type routes), no entity instances (CORE-ENT-002), no relationships
/// (CORE-ENT-003), no template loading (CORE-ENT-004) and no search/visibility filtering
/// (CORE-ENT-005); those are later stories and are deliberately not built here. This contract
/// takes explicit ids; resolving the "current" organization or workspace from a request is the
/// tenant context resolver and later stories.
/// </summary>
public interface IEntityTypeRepository
{
    /// <summary>
    /// Finds the entity type with exactly the given id WITHIN the given organization and
    /// workspace, or <see langword="null"/> when no such type exists there. The organization
    /// and workspace both scope the lookup, so a type that exists under another organization's
    /// or workspace's id is never returned, even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity type id is empty. An empty id can never
    /// address a stored entity type, so the lookup is rejected instead of silently returning
    /// nothing.
    /// </exception>
    Task<EntityType?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the entity type addressed by exactly the given key WITHIN the given organization
    /// and workspace, or <see langword="null"/> when no such type exists there. The key is
    /// canonicalized (and malformed input rejected) before the lookup, so a malformed value
    /// can never address a type. The organization and workspace both scope the lookup, so the
    /// same key in another workspace or tenant is never returned (threat T5/T1). Backs the
    /// per-workspace uniqueness of the type key.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, or the type key is not a valid key after
    /// canonicalization.
    /// </exception>
    Task<EntityType?> FindByKeyAsync(
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every entity type of the given workspace (owned by the given organization) in a
    /// deterministic order — sorted by the canonical type key so the sequence is stable and
    /// repeatable. The list is tenant- AND workspace-scoped: the predicate leads with
    /// <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign tenant's or a
    /// foreign workspace's types are NEVER returned even when their ids would otherwise be
    /// addressable (threat T5/T1; the organization boundary is checked before the workspace
    /// boundary). An empty list is returned for a workspace that defines no types; the lookup
    /// never crosses the tenant or workspace boundary to borrow another workspace's types.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a stored
    /// workspace's types, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<EntityType>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new entity type. Returns <see cref="EntityTypeAddResult.Added"/> on success
    /// or <see cref="EntityTypeAddResult.DuplicateKey"/> when the workspace already defines a
    /// type with the same key (the per-workspace unique (<c>workspace_id</c>, <c>type_key</c>)
    /// index rejected the insert). The existing row is left unchanged. Foreign-key violations
    /// (a non-existent workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<EntityTypeAddResult> AddAsync(EntityType entityType, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an entity type previously loaded through this repository. The
    /// organization, workspace, id and type key of an entity type are immutable
    /// (<see cref="EntityType"/>), so an update only ever changes the display name, the
    /// attribute schema and the update timestamp; it can never move the type to another tenant
    /// or workspace, nor change its natural key (threat T5). The caller is responsible for
    /// having loaded the type through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(EntityType entityType, CancellationToken cancellationToken);
}
