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
    public async Task<QuotaUsageConsumeResult> TryConsumeAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        long amount,
        long? limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A consumed amount must be strictly positive.");
        }

        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A quota limit must not be negative.");
        }

        // A capped grant whose very first unit overruns the cap can never be consumed (a fresh row would start
        // over the cap, and an existing row is already at/over it), so it is rejected before any write.
        if (limit is { } cap && amount > cap)
        {
            return QuotaUsageConsumeResult.LimitExceeded;
        }

        // The atomic check-and-consume is a SINGLE database UPSERT (INSERT ... ON CONFLICT ... DO UPDATE), so two
        // concurrent commands can never both pass the cap and it never throws on a concurrent insert (so it is safe
        // inside an outer transaction). When no usage row exists the INSERT creates it at the consumed amount (legal
        // because the over-cap first unit was rejected above); when one exists the conflict path increments it only
        // when the limit-guard holds (re-evaluated by the database against the row it locks). Zero rows affected
        // means the existing row had no headroom -> over the limit. The subject type is passed as its stored string
        // name; every value is a parameter.
        var subjectTypeName = subjectType.ToString();
        var newId = Guid.CreateVersion7();
        var key = definition.EntitlementKey;
        var definitionId = definition.Id;

        int affected;
        if (limit is { } capValue)
        {
            affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO quota_usage
                       (id, subject_type, subject_id, quota_definition_id, entitlement_key, used_amount, created_at, updated_at)
                   VALUES
                       ({newId}, {subjectTypeName}, {subjectId}, {definitionId}, {key}, {amount}, {now}, {now})
                   ON CONFLICT (subject_type, subject_id, quota_definition_id)
                   DO UPDATE SET used_amount = quota_usage.used_amount + {amount}, updated_at = {now}
                   WHERE quota_usage.used_amount + {amount} <= {capValue}",
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Unlimited (fair-use) grant: the increment is unconditional (no cap guard).
            affected = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO quota_usage
                       (id, subject_type, subject_id, quota_definition_id, entitlement_key, used_amount, created_at, updated_at)
                   VALUES
                       ({newId}, {subjectTypeName}, {subjectId}, {definitionId}, {key}, {amount}, {now}, {now})
                   ON CONFLICT (subject_type, subject_id, quota_definition_id)
                   DO UPDATE SET used_amount = quota_usage.used_amount + {amount}, updated_at = {now}",
                cancellationToken).ConfigureAwait(false);
        }

        return affected > 0 ? QuotaUsageConsumeResult.Consumed : QuotaUsageConsumeResult.LimitExceeded;
    }

    /// <inheritdoc />
    public async Task<long> GetUsedAmountAsync(
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

        // No-tracking read so the post-consume value is the committed row, never a stale tracked snapshot from an
        // earlier read in this scope; scoped by the subject pair then the quota, so it never returns another
        // subject's usage.
        return await _dbContext.QuotaUsage
            .AsNoTracking()
            .Where(usage => usage.SubjectType == subjectType
                && usage.SubjectId == subjectId
                && usage.QuotaDefinitionId == quotaDefinitionId)
            .Select(usage => (long?)usage.UsedAmount)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L;
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
