using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entities;

/// <summary>
/// EF Core implementation of <see cref="IEntityTypeRepository"/> (CORE-ENT-001), backed by
/// the <c>entity_types</c> table mapped in <see cref="EntityTypeConfiguration"/>.
/// </summary>
internal sealed class EntityTypeRepository : IEntityTypeRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public EntityTypeRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<EntityType?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored entity type (ids are generated non-empty), so
        // the lookup fails fast instead of returning an arbitrary row.
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
            throw new ArgumentException("Entity type id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with the
        // tenant column. The lookup is exactly tenant- and workspace-scoped, so a type under
        // another organization or workspace is never returned even when the surrogate id
        // matches (threat T5/T1).
        return await _dbContext.EntityTypes
            .FirstOrDefaultAsync(
                entityType => entityType.OrganizationId == organizationId
                    && entityType.WorkspaceId == workspaceId
                    && entityType.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntityType?> FindByKeyAsync(
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // Canonicalize first (and reject malformed input): a stored key is always canonical,
        // so a malformed value can never address a type.
        var canonicalKey = EntityType.CanonicalizeTypeKey(typeKey);

        // The predicate translates to parameterized SQL equality on the unique
        // (organization_id, workspace_id, type_key) index, which is exact and case-sensitive
        // under the default binary-style collations: a near-match, or the same key in another
        // workspace or tenant, never addresses a foreign type (threat T5). The unique index
        // guarantees at most one row per (organization, workspace, key).
        return await _dbContext.EntityTypes
            .FirstOrDefaultAsync(
                entityType => entityType.OrganizationId == organizationId
                    && entityType.WorkspaceId == workspaceId
                    && entityType.TypeKey == canonicalKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntityType>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's types, so the lookup fails fast
        // instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace, so the
        // list is exactly tenant- and workspace-scoped: another tenant's or another
        // workspace's types are never returned even when their ids would otherwise be
        // addressable (threat T5/T1; the organization boundary is checked before the workspace
        // boundary). The ordering is deterministic — sorted by the canonical type key, which
        // is unique within the workspace, so the sequence is stable and repeatable.
        return await _dbContext.EntityTypes
            .Where(entityType => entityType.OrganizationId == organizationId
                && entityType.WorkspaceId == workspaceId)
            .OrderBy(entityType => entityType.TypeKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntityTypeAddResult> AddAsync(
        EntityType entityType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        _dbContext.EntityTypes.Add(entityType);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return EntityTypeAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later
            // SaveChanges on the same scope.
            _dbContext.Entry(entityType).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this key exists now in the
            // same workspace, the unique (organization_id, workspace_id, type_key) index
            // rejected the insert as a duplicate (typically a lost create race). Any other
            // failure (for example a foreign-key violation for a non-existent workspace or
            // tenant) is rethrown unchanged.
            var duplicateExists = await _dbContext.EntityTypes
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.OrganizationId == entityType.OrganizationId
                        && existing.WorkspaceId == entityType.WorkspaceId
                        && existing.TypeKey == entityType.TypeKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return EntityTypeAddResult.DuplicateKey;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(EntityType entityType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        // The entity type was loaded and mutated within this scope's change tracker (or is
        // attached here); only the mutable display name, attribute schema and update timestamp
        // change. The organization, workspace, id and type key are immutable on the aggregate,
        // so an update can never move the row to another tenant or workspace, nor change the
        // natural key (threat T5).
        _dbContext.EntityTypes.Update(entityType);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
