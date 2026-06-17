// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// EF Core implementation of <see cref="IEntitlementDefinitionRepository"/> (CORE-ENTL-001), backed by the
/// global <c>entitlement_definitions</c> table mapped in <see cref="EntitlementDefinitionConfiguration"/>.
/// </summary>
internal sealed class EntitlementDefinitionRepository : IEntitlementDefinitionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public EntitlementDefinitionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<EntitlementDefinitionAddResult> AddAsync(
        EntitlementDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _dbContext.EntitlementDefinitions.Add(definition);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return EntitlementDefinitionAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the
            // same scope.
            _dbContext.Entry(definition).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a row with this key exists now, the unique index
            // rejected the insert as a duplicate (typically a lost create race). Any other failure is rethrown
            // unchanged.
            var duplicateExists = await _dbContext.EntitlementDefinitions
                .AsNoTracking()
                .AnyAsync(existing => existing.Key == definition.Key, cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return EntitlementDefinitionAddResult.DuplicateKey;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<EntitlementDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // An empty id can never address a stored definition (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(id));
        }

        return await _dbContext.EntitlementDefinitions
            .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitlementDefinition?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        // Canonicalize first (and reject malformed input): a stored key is always canonical, so a malformed
        // value can never address a definition.
        var canonicalKey = EntitlementDefinition.CanonicalizeKey(key);

        return await _dbContext.EntitlementDefinitions
            .FirstOrDefaultAsync(definition => definition.Key == canonicalKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EntitlementDefinition>> ListAllAsync(CancellationToken cancellationToken)
        => await _dbContext.EntitlementDefinitions
            .OrderBy(definition => definition.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
