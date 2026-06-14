namespace LiveCore.Api.Entitlements;

/// <summary>
/// Outcome of an ATOMIC check-and-consume of a subject's quota usage through
/// <see cref="IQuotaUsageRepository.TryConsumeAsync"/> (CORE-CONC-004). The check (does the requested amount fit
/// the granted limit?) and the consume (the increment) are a SINGLE limit-guarded statement, so two concurrent
/// commands can never both pass the limit (the time-of-check-to-time-of-use race the separate read-then-write had).
/// </summary>
public enum QuotaUsageConsumeResult
{
    /// <summary>
    /// The amount was consumed: the limit-guarded increment applied (or, for an unlimited grant, the unconditional
    /// increment applied), so the recorded usage now includes this consumption.
    /// </summary>
    Consumed = 1,

    /// <summary>
    /// The amount was NOT consumed because it would have exceeded the granted limit. Nothing was recorded; the
    /// usage is unchanged. A concurrent command that consumed the last unit first lands here.
    /// </summary>
    LimitExceeded = 2,
}
