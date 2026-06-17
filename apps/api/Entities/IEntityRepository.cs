// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Persistence contract for the entity instance aggregate (CORE-ENT-002). The Entities module
/// owns the <c>entities</c> table; other modules access entities only through this contract or the
/// module's application services (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Entities module owns "generic entities").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and
/// the workspace id, and an entity is only ever returned when it belongs to exactly that
/// (organization, workspace) pair. The organization boundary is checked before the workspace
/// boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so an entity is never
/// returned through a foreign organization's id even when the workspace and ids are correct, and
/// never through a foreign workspace's id even when the organization and ids are correct. There is
/// deliberately no lookup of an entity by id alone, no lookup that crosses tenants, and NO
/// list-everything method, so one workspace's entity can never be read through another workspace's
/// id and an entity in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// SAME-WORKSPACE-TYPE coupling: <see cref="ListByTypeAsync"/> filters by an
/// <c>entity_type_id</c> within an explicit (organization, workspace) scope, so it never borrows
/// another workspace's entities. The entity's own <c>entity_type_id</c> foreign key guarantees the
/// referenced type EXISTS but not that the type is in the entity's workspace; ensuring an entity is
/// only ever created against a type IN ITS OWN WORKSPACE is the responsibility of the future
/// create-entity application flow (it resolves the type through the workspace-scoped
/// <see cref="IEntityTypeRepository.FindByIdAsync"/> before creating the entity), mirroring how the
/// content-block create flow resolves the scene. This repository does not perform that
/// cross-aggregate check.
///
/// This is the aggregate + persistence story. There are NO HTTP endpoints (csv/api_routes.csv
/// defines no entity route), no entity relationships (CORE-ENT-003), no template loading or
/// schema-conformance validation (CORE-ENT-004) and no search/visibility filtering (CORE-ENT-005);
/// those are later stories and are deliberately not built here. This contract takes explicit ids;
/// resolving the "current" organization or workspace from a request is the tenant context resolver
/// and later stories.
/// </summary>
public interface IEntityRepository
{
    /// <summary>
    /// Finds the entity with exactly the given id WITHIN the given organization and workspace, or
    /// <see langword="null"/> when no such entity exists there. The organization and workspace both
    /// scope the lookup, so an entity that exists under another organization's or workspace's id is
    /// never returned, even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity id is empty. An empty id can never address a
    /// stored entity, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<Entity?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every entity of the given workspace (owned by the given organization) in a
    /// deterministic order — sorted by the surrogate id, which is time-ordered (UUIDv7), so the
    /// sequence is stable and repeatable. The list is tenant- AND workspace-scoped: the predicate
    /// leads with <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign
    /// tenant's or a foreign workspace's entities are NEVER returned even when their ids would
    /// otherwise be addressable (threat T5/T1; the organization boundary is checked before the
    /// workspace boundary). An empty list is returned for a workspace that has no entities; the
    /// lookup never crosses the tenant or workspace boundary to borrow another workspace's
    /// entities. This is NOT a list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a stored
    /// workspace's entities, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<Entity>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the given workspace's entities (owned by the given organization) in the same
    /// deterministic (UUIDv7 id) order as <see cref="ListByWorkspaceAsync"/>, limited to <paramref name="take"/>
    /// rows starting at <paramref name="skip"/>, backing the bounded entity list route
    /// (<c>GET /api/v1/workspaces/{workspaceId}/entities</c>, CORE-DX-003). The page is exactly tenant- AND
    /// workspace-scoped (the predicate leads with <c>organization_id</c> then matches <c>workspace_id</c>), so a
    /// foreign tenant's or workspace's entities are NEVER returned (threat T5/T1). The caller over-fetches one
    /// extra row (<c>take = limit + 1</c>) to decide <c>hasMore</c> without a second COUNT. Bounding the read
    /// means a single list response can never materialize the whole table (threat T9).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<Entity>> ListPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every entity of the given entity type WITHIN the given organization and workspace in a
    /// deterministic order — sorted by the surrogate id (UUIDv7, time-ordered), so the sequence is
    /// stable and repeatable. The list is tenant-, workspace- AND type-scoped: the predicate leads
    /// with <c>organization_id</c>, then matches <c>workspace_id</c> and <c>entity_type_id</c>, so
    /// a foreign tenant's or workspace's entities are NEVER returned even when their ids would
    /// otherwise be addressable (threat T5/T1). An empty list is returned for a type with no
    /// instances in the workspace; the lookup never crosses a boundary to borrow another
    /// workspace's entities. The type id is matched as a plain column value, so an instance whose
    /// type lives in a DIFFERENT workspace (a same-workspace-type coupling the application layer is
    /// responsible for) would still be returned here for the workspace it is stored in — this
    /// method scopes by the entity's own (organization, workspace) only and does not re-validate
    /// the type's workspace.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity type id is empty. An empty id can never address
    /// a stored type's entities, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<Entity>> ListByTypeAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new entity. An entity has no natural key (it is identified only by its surrogate
    /// id), so there is no uniqueness outcome to report; the result is always
    /// <see cref="EntityAddResult.Added"/> on success. Foreign-key violations (a non-existent
    /// entity type, workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<EntityAddResult> AddAsync(Entity entity, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an entity previously loaded through this repository — in particular a
    /// rename or a redefined attribute-values document. The organization, workspace, id and entity
    /// type of an entity are immutable (<see cref="Entity"/>), so an update only ever changes the
    /// name, the attribute values and the update timestamp; it can never move the entity to another
    /// tenant or workspace, nor change its type linkage (threat T5). The caller is responsible for
    /// having loaded the entity through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(Entity entity, CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES the given entity row (CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic).
    /// The caller (<see cref="EntityDeletionService"/>) is responsible for having loaded the entity
    /// through the tenant- AND workspace-scoped <see cref="FindByIdAsync"/> first, so a foreign tenant's
    /// or foreign workspace's entity is never reachable to remove (threat T5/T1); this method removes
    /// exactly the row it is handed. Removing the entity DOES cascade at the database level to its
    /// directed <see cref="EntityRelationship"/> edges (both endpoint foreign keys are
    /// <c>ON DELETE CASCADE</c>); the dependent <c>visibility_rules</c> and <c>asset_links</c> reference
    /// the entity POLYMORPHICALLY (no foreign key) so the database does not cascade them — the
    /// application <see cref="EntityDeletionService"/> removes those (and, for determinism, the edges
    /// too) inside the SAME unit of work BEFORE this call, so the cascade is consistent across providers
    /// (docs/adr/0012-resource-deletion-cascades-dependents.md).
    /// </summary>
    /// <exception cref="ArgumentNullException">The entity is null.</exception>
    Task RemoveAsync(Entity entity, CancellationToken cancellationToken);
}
