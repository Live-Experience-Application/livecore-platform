// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// EF Core implementation of <see cref="IQuotaDefinitionRepository"/> (CORE-ENTL-003), backed by the global
/// <c>quota_definitions</c> table mapped in <see cref="QuotaDefinitionConfiguration"/>.
/// </summary>
internal sealed class QuotaDefinitionRepository : IQuotaDefinitionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public QuotaDefinitionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<QuotaDefinitionAddResult> AddAsync(QuotaDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        _dbContext.QuotaDefinitions.Add(definition);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return QuotaDefinitionAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope.
            _dbContext.Entry(definition).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a quota for this entitlement exists now, the unique
            // (entitlement_definition_id) index rejected the insert as a duplicate (typically a lost create race).
            // Any other failure (for example a measured entitlement-definition foreign key that does not exist) is
            // rethrown unchanged.
            var duplicateExists = await _dbContext.QuotaDefinitions
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.EntitlementDefinitionId == definition.EntitlementDefinitionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return QuotaDefinitionAddResult.DuplicateEntitlement;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<QuotaDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        // An empty id can never address a stored definition (ids are generated non-empty), so the lookup fails
        // fast instead of returning an arbitrary row.
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Quota definition id must not be empty.", nameof(id));
        }

        return await _dbContext.QuotaDefinitions
            .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuotaDefinition>> ListActiveBySubjectTypeAsync(
        EntitlementSubjectType subjectType,
        CancellationToken cancellationToken)
        => await _dbContext.QuotaDefinitions
            .Where(definition => definition.SubjectType == subjectType && definition.IsActive)
            .OrderBy(definition => definition.EntitlementKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuotaDefinition>> ListAllAsync(CancellationToken cancellationToken)
        => await _dbContext.QuotaDefinitions
            .OrderBy(definition => definition.EntitlementKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
