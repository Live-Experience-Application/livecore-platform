namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of persisting a new <see cref="QuotaUsage"/> through <see cref="IQuotaUsageRepository.AddAsync"/>
/// (CORE-ENTL-003). A subject records each quota at most once, so a second usage row for the same (subject, quota)
/// is reported rather than throwing, mirroring <see cref="SubjectEntitlementAddResult"/>.
/// </summary>
public enum QuotaUsageAddResult
{
    /// <summary>The usage row was inserted.</summary>
    Added = 1,

    /// <summary>
    /// A usage row for the same (subject, quota) already exists (the unique <c>quota_usage(subject_type,
    /// subject_id, quota_definition_id)</c> index rejected the insert), typically a lost create race. Nothing was
    /// inserted.
    /// </summary>
    DuplicateUsage = 2,
}
