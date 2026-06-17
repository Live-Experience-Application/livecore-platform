// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// EF Core implementation of <see cref="IPlanDefinitionRepository"/> (CORE-ENTL-001), backed by the global
/// <c>plan_definitions</c> table (with its <c>plan_entitlements</c> child rows) mapped in
/// <see cref="PlanDefinitionConfiguration"/> and <see cref="PlanEntitlementConfiguration"/>.
/// </summary>
internal sealed class PlanDefinitionRepository : IPlanDefinitionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public PlanDefinitionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<PlanDefinitionAddResult> AddAsync(PlanDefinition plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _dbContext.PlanDefinitions.Add(plan);
        try
        {
            // The plan and its grants are saved as one graph; EF inserts the parent row and the child grant
            // rows in one transaction.
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return PlanDefinitionAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the
            // same scope.
            _dbContext.Entry(plan).State = EntityState.Detached;
            foreach (var grant in plan.Entitlements)
            {
                _dbContext.Entry(grant).State = EntityState.Detached;
            }

            // Provider-neutral duplicate detection: if a row with this key exists now, the unique index
            // rejected the insert as a duplicate (typically a lost create race). Any other failure (for
            // example a granted entitlement-definition foreign key that does not exist) is rethrown unchanged.
            var duplicateExists = await _dbContext.PlanDefinitions
                .AsNoTracking()
                .AnyAsync(existing => existing.Key == plan.Key, cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return PlanDefinitionAddResult.DuplicateKey;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PlanDefinition?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Plan definition id must not be empty.", nameof(id));
        }

        return await _dbContext.PlanDefinitions
            .Include(plan => plan.Entitlements)
            .FirstOrDefaultAsync(plan => plan.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlanDefinition?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        var canonicalKey = PlanDefinition.CanonicalizeKey(key);

        return await _dbContext.PlanDefinitions
            .Include(plan => plan.Entitlements)
            .FirstOrDefaultAsync(plan => plan.Key == canonicalKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlanDefinition>> ListAllAsync(CancellationToken cancellationToken)
        => await _dbContext.PlanDefinitions
            .Include(plan => plan.Entitlements)
            .OrderBy(plan => plan.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
