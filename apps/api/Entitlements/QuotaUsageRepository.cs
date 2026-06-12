using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// EF Core implementation of <see cref="IQuotaUsageRepository"/> (CORE-ENTL-003), backed by the <c>quota_usage</c>
/// table mapped in <see cref="QuotaUsageConfiguration"/>. Every read leads with the (subject_type, subject_id)
/// pair, so one subject's usage is never returned through another subject's id.
/// </summary>
internal sealed class QuotaUsageRepository : IQuotaUsageRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public QuotaUsageRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<QuotaUsageAddResult> AddAsync(QuotaUsage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);

        _dbContext.QuotaUsage.Add(usage);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return QuotaUsageAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope.
            _dbContext.Entry(usage).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if the subject already records this quota, the unique
            // (subject_type, subject_id, quota_definition_id) index rejected the insert as a duplicate (typically
            // a lost create race). Any other failure (for example a quota-definition foreign key that does not
            // exist) is rethrown unchanged.
            var duplicateExists = await _dbContext.QuotaUsage
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.SubjectType == usage.SubjectType
                        && existing.SubjectId == usage.SubjectId
                        && existing.QuotaDefinitionId == usage.QuotaDefinitionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return QuotaUsageAddResult.DuplicateUsage;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(QuotaUsage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usage);

        // The usage row is already tracked (it was loaded through this context); EF persists the mutated amount.
        // Update is in place — a subject never carries two rows for the same quota.
        _dbContext.QuotaUsage.Update(usage);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<QuotaUsage?> FindBySubjectAndQuotaAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid quotaDefinitionId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (quotaDefinitionId == Guid.Empty)
        {
            throw new ArgumentException("Quota definition id must not be empty.", nameof(quotaDefinitionId));
        }

        // The predicate leads with the subject pair, then the quota, so another subject's usage is never returned
        // even when the quota id matches.
        return await _dbContext.QuotaUsage
            .FirstOrDefaultAsync(
                usage => usage.SubjectType == subjectType
                    && usage.SubjectId == subjectId
                    && usage.QuotaDefinitionId == quotaDefinitionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuotaUsage>> ListBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        // Scoped by the subject pair, ordered by the stable entitlement key for a deterministic result (a value
        // supported by every provider, unlike a DateTimeOffset ORDER BY on SQLite).
        return await _dbContext.QuotaUsage
            .Where(usage => usage.SubjectType == subjectType && usage.SubjectId == subjectId)
            .OrderBy(usage => usage.EntitlementKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
