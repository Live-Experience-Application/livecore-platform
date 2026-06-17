// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entities;

/// <summary>
/// EF Core implementation of <see cref="IEntityRelationshipRepository"/> (CORE-ENT-003), backed by
/// the <c>entity_relationships</c> table mapped in <see cref="EntityRelationshipConfiguration"/>.
/// </summary>
internal sealed class EntityRelationshipRepository : IEntityRelationshipRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public EntityRelationshipRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<EntityRelationship?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored relationship (ids are generated non-empty), so the
        // lookup fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity relationship id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant
        // column. The lookup is exactly tenant- and workspace-scoped, so an edge under another
        // organization or workspace is never returned even when the surrogate id matches (threat
        // T5/T1).
        return await _dbContext.EntityRelationships
            .FirstOrDefaultAsync(
                relationship => relationship.OrganizationId == organizationId
                    && relationship.WorkspaceId == workspaceId
                    && relationship.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityRelationship>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's relationships, so the lookup fails fast
        // instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the list is
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's edges are
        // never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering is
        // deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.EntityRelationships
            .Where(relationship => relationship.OrganizationId == organizationId
                && relationship.WorkspaceId == workspaceId)
            .OrderBy(relationship => relationship.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityRelationship>> ListBySourceEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored entity's edges, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sourceEntityId == Guid.Empty)
        {
            throw new ArgumentException("Source entity id must not be empty.", nameof(sourceEntityId));
        }

        // The predicate leads with the tenant column, then matches the workspace and the source, so
        // the list is exactly tenant-, workspace- and source-scoped: another tenant's or workspace's
        // edges are never returned even when their ids would otherwise be addressable (threat T5/T1).
        // The edge is directed, so this returns the OUTGOING edges of the entity only. The ordering
        // is deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.EntityRelationships
            .Where(relationship => relationship.OrganizationId == organizationId
                && relationship.WorkspaceId == workspaceId
                && relationship.SourceEntityId == sourceEntityId)
            .OrderBy(relationship => relationship.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityRelationship>> ListByEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored entity's edges, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
        }

        // The predicate leads with the tenant column, then matches the workspace, then matches the
        // entity as EITHER endpoint (source OR target): this is the undirected "all edges touching
        // this entity" listing. It stays exactly tenant- and workspace-scoped, so another tenant's or
        // workspace's edges are never returned (threat T5/T1). This is a simple scoped list, NOT a
        // graph traversal (multi-hop traversal is CORE-ENT-005). The ordering is deterministic —
        // sorted by the time-ordered surrogate id.
        return await _dbContext.EntityRelationships
            .Where(relationship => relationship.OrganizationId == organizationId
                && relationship.WorkspaceId == workspaceId
                && (relationship.SourceEntityId == entityId || relationship.TargetEntityId == entityId))
            .OrderBy(relationship => relationship.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntityRelationshipAddResult> AddAsync(
        EntityRelationship relationship,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        _dbContext.EntityRelationships.Add(relationship);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return EntityRelationshipAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges
            // on the same scope.
            _dbContext.Entry(relationship).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if the same directed edge of the same kind
            // exists now in the same workspace, the unique (workspace_id, source_entity_id,
            // target_entity_id, relationship_kind) index rejected the insert as a duplicate
            // (typically a lost create race). Any other failure (for example a foreign-key violation
            // for a non-existent endpoint, workspace or tenant) is rethrown unchanged.
            var duplicateExists = await _dbContext.EntityRelationships
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.WorkspaceId == relationship.WorkspaceId
                        && existing.SourceEntityId == relationship.SourceEntityId
                        && existing.TargetEntityId == relationship.TargetEntityId
                        && existing.RelationshipKind == relationship.RelationshipKind,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return EntityRelationshipAddResult.Duplicate;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        EntityRelationship relationship,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        // The caller resolved this exact row through the tenant- and workspace-scoped FindByIdAsync,
        // so removing the loaded entity deletes precisely that one edge. Only the relationship row is
        // touched — the source and target entities are left intact (this is the inverse of the
        // per-edge insert, not a cascade from an endpoint).
        _dbContext.EntityRelationships.Remove(relationship);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> RemoveByEntityAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored entity's edges, so the deletion fails fast instead of
        // matching an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
        }

        // The predicate leads with the tenant column, then matches the workspace, then matches the
        // entity as EITHER endpoint (source OR target): every edge touching the entity, in BOTH
        // directions. It stays exactly tenant- and workspace-scoped, so another tenant's or workspace's
        // edges are never removed even when the entity id would otherwise be addressable (threat T5/T1).
        // Load-then-RemoveRange (the codebase's add/remove + SaveChanges convention) so the removed rows
        // participate in any ambient transaction the EntityDeletionService controls.
        var edges = await _dbContext.EntityRelationships
            .Where(relationship => relationship.OrganizationId == organizationId
                && relationship.WorkspaceId == workspaceId
                && (relationship.SourceEntityId == entityId || relationship.TargetEntityId == entityId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (edges.Count == 0)
        {
            return 0;
        }

        _dbContext.EntityRelationships.RemoveRange(edges);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return edges.Count;
    }
}
