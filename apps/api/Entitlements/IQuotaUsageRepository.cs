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
    /// ATOMICALLY checks-and-consumes <paramref name="amount"/> of the subject's usage of the given quota, so two
    /// concurrent commands can never both pass the limit (CORE-CONC-004). The check (does the consumption fit the
    /// granted <paramref name="limit"/>?) and the consume (the increment) are a SINGLE limit-guarded statement
    /// (<c>UPDATE ... SET used = used + amount WHERE ... AND used + amount &lt;= limit</c>) — never a read followed
    /// by a separate write — so the time-of-check-to-time-of-use race is closed: a concurrent writer re-evaluates
    /// the guard against the just-committed row and is rejected rather than overrunning the cap.
    ///
    /// <para>
    /// When no usage row exists yet, the first consumption inserts it at <paramref name="amount"/> (only when that
    /// fits the limit); the unique per-subject index makes a concurrent insert a safe lost-create race that re-tries
    /// the guarded increment. A <see langword="null"/> <paramref name="limit"/> is an UNLIMITED (fair-use) grant: the
    /// increment is unconditional and always <see cref="QuotaUsageConsumeResult.Consumed"/>.
    /// </para>
    /// </summary>
    /// <param name="subjectType">The kind of subject the usage is recorded for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="definition">The quota definition whose usage is consumed (supplies the key/id for a first-insert).</param>
    /// <param name="amount">The amount to consume (strictly positive).</param>
    /// <param name="limit">The granted numeric cap (non-negative), or <see langword="null"/> for an unlimited grant.</param>
    /// <param name="now">The timestamp to stamp on the recorded usage.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<QuotaUsageConsumeResult> TryConsumeAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        QuotaDefinition definition,
        long amount,
        long? limit,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the subject's currently recorded usage of the given quota (zero when no usage row exists). A
    /// no-tracking read scoped by the subject pair then the quota, so it never returns another subject's usage and
    /// never disturbs the change tracker — used to report the post-consume usage on an enforcement decision.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id or quota definition id is empty.</exception>
    Task<long> GetUsedAmountAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        Guid quotaDefinitionId,
        CancellationToken cancellationToken);

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
