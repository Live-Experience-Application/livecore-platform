// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entities;

/// <summary>
/// EF Core implementation of <see cref="IEntityRepository"/> (CORE-ENT-002), backed by the
/// <c>entities</c> table mapped in <see cref="EntityConfiguration"/>.
/// </summary>
internal sealed class EntityRepository : IEntityRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public EntityRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Entity?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored entity (ids are generated non-empty), so the lookup
        // fails fast instead of returning an arbitrary row.
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
            throw new ArgumentException("Entity id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the tenant
        // column. The lookup is exactly tenant- and workspace-scoped, so an entity under another
        // organization or workspace is never returned even when the surrogate id matches (threat
        // T5/T1).
        return await _dbContext.Entities
            .FirstOrDefaultAsync(
                entity => entity.OrganizationId == organizationId
                    && entity.WorkspaceId == workspaceId
                    && entity.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entity>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's entities, so the lookup fails fast
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
        // exactly tenant- and workspace-scoped: another tenant's or another workspace's entities
        // are never returned even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering is
        // deterministic — sorted by the surrogate id, which is time-ordered (UUIDv7), so the
        // sequence is stable and repeatable.
        return await _dbContext.Entities
            .Where(entity => entity.OrganizationId == organizationId
                && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entity>> ListPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's entities, so the lookup fails fast (mirrors the
        // unbounded list).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // The same tenant- AND workspace-scoped, deterministic (UUIDv7 id) read as ListByWorkspaceAsync
        // (predicate leads with organization_id then workspace_id, threat T5/T1), but bounded by Skip/Take so an
        // unbounded list is never materialized (threat T9).
        return await _dbContext.Entities
            .Where(entity => entity.OrganizationId == organizationId
                && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Entity>> ListByTypeAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored type's entities, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (entityTypeId == Guid.Empty)
        {
            throw new ArgumentException("Entity type id must not be empty.", nameof(entityTypeId));
        }

        // The predicate leads with the tenant column, then matches the workspace and the type, so
        // the list is exactly tenant-, workspace- and type-scoped: another tenant's or workspace's
        // entities are never returned even when their ids would otherwise be addressable (threat
        // T5/T1; the organization boundary is checked before the workspace boundary). The ordering
        // is deterministic — sorted by the time-ordered surrogate id.
        return await _dbContext.Entities
            .Where(entity => entity.OrganizationId == organizationId
                && entity.WorkspaceId == workspaceId
                && entity.EntityTypeId == entityTypeId)
            .OrderBy(entity => entity.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntityAddResult> AddAsync(Entity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        _dbContext.Entities.Add(entity);

        // An entity has no uniqueness constraint to violate, so there is no duplicate outcome to
        // translate here; a foreign-key violation (a non-existent entity type, workspace or tenant)
        // propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return EntityAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Entity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // The entity was loaded and mutated within this scope's change tracker (or is attached
        // here); only the mutable name, attribute values and update timestamp change. The
        // organization, workspace, id and entity type are immutable on the aggregate, so an update
        // can never move the row to another tenant or workspace, nor change its type linkage
        // (threat T5).
        _dbContext.Entities.Update(entity);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Entity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // The caller resolved this exact row through the tenant- and workspace-scoped FindByIdAsync,
        // so removing the loaded entity deletes precisely that one entity. The database FKs cascade
        // this delete to the entity's directed relationship edges; the polymorphic visibility_rules and
        // asset_links are removed by EntityDeletionService in the same unit of work (the database cannot
        // cascade a non-FK reference). See docs/adr/0012-resource-deletion-cascades-dependents.md.
        _dbContext.Entities.Remove(entity);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
