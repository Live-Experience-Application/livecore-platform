namespace LiveCore.Api.Entitlements;

/// <summary>
/// Persistence contract for the quota usage aggregate (CORE-ENTL-003). The Entitlements module owns the
/// <c>quota_usage</c> table; other modules access usage only through this contract or the module's application
/// services (docs/02_ARCHITECTURE.md: no direct table ownership violations).
///
/// Every read is scoped by the (<see cref="QuotaUsage.SubjectType"/>, <see cref="QuotaUsage.SubjectId"/>) pair, so
/// one subject's usage can never be read through another subject's id, and a user subject and a workspace subject
/// that share a guid never collide (the per-subject isolation that backs the quota-status calculation). There is
/// deliberately NO list-everything read: quota usage is always addressed by its subject.
/// </summary>
public interface IQuotaUsageRepository
{
    /// <summary>
    /// Persists a new usage row. A subject records each quota at most once, so a row that duplicates an existing
    /// (subject, quota) pair is reported as <see cref="QuotaUsageAddResult.DuplicateUsage"/>; any other failure
    /// (for example a quota-definition foreign key that does not exist) surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<QuotaUsageAddResult> AddAsync(QuotaUsage usage, CancellationToken cancellationToken);

    /// <summary>Persists changes to an already-tracked usage row (a recorded amount change).</summary>
    Task UpdateAsync(QuotaUsage usage, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the subject's usage of exactly the given quota definition, or <see langword="null"/> when the subject
    /// has none. The lookup is scoped by the subject pair then the quota, so it never returns another subject's
    /// usage.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id or quota definition id is empty.</exception>
    Task<QuotaUsage?> FindBySubjectAndQuotaAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid quotaDefinitionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every usage row recorded for the given subject, in a deterministic order. Scoped by the subject pair,
    /// so it never returns another subject's usage. This is the read the quota-status calculation consumes.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id is empty.</exception>
    Task<IReadOnlyList<QuotaUsage>> ListBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);
}
