namespace LiveCore.Api.Retention;

/// <summary>
/// The outcome of one data-retention family's purge during a sweep (CORE-PRIV-003). It records counts only —
/// never any record id or content — so the worker can log a sweep summary safely (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Examined">How many candidate records the family's purge looked at this run.</param>
/// <param name="Purged">
/// How many records were purged: their audit fact was appended and the record (and its cascade) was removed,
/// committed atomically.
/// </param>
/// <param name="Failed">
/// How many records could not be purged this run because a step failed (a transient persistence/storage error,
/// or a concurrent purge by another worker). Nothing partial is left behind — the transaction rolled back — so
/// the next sweep retries.
/// </param>
public readonly record struct DataRetentionFamilyResult(int Examined, int Purged, int Failed)
{
    /// <summary>An all-zero result, used for a family whose purge is disabled (it examined nothing).</summary>
    public static DataRetentionFamilyResult None => new(0, 0, 0);
}

/// <summary>
/// The outcome of one full run of the background data-retention sweep (CORE-PRIV-003/CORE-PRIV-006): the
/// per-family <see cref="DataRetentionFamilyResult"/> for each of the five families. Counts only, so a sweep
/// summary is safe to log (threat T7).
/// </summary>
/// <param name="Sessions">The completed/expired session family's result.</param>
/// <param name="Recaps">The generated recap family's result.</param>
/// <param name="Exports">The completed export artifact family's result.</param>
/// <param name="Invitations">The closed/expired/revoked invitation family's result.</param>
/// <param name="IdempotencyKeys">The idempotency-key family's result (CORE-PRIV-006); a count-only bulk purge by age.</param>
public readonly record struct DataRetentionSweepResult(
    DataRetentionFamilyResult Sessions,
    DataRetentionFamilyResult Recaps,
    DataRetentionFamilyResult Exports,
    DataRetentionFamilyResult Invitations,
    DataRetentionFamilyResult IdempotencyKeys)
{
    /// <summary>The total number of candidate records examined across all families this run.</summary>
    public int TotalExamined =>
        Sessions.Examined + Recaps.Examined + Exports.Examined + Invitations.Examined + IdempotencyKeys.Examined;

    /// <summary>The total number of records purged across all families this run.</summary>
    public int TotalPurged =>
        Sessions.Purged + Recaps.Purged + Exports.Purged + Invitations.Purged + IdempotencyKeys.Purged;

    /// <summary>The total number of records that could not be purged across all families this run.</summary>
    public int TotalFailed =>
        Sessions.Failed + Recaps.Failed + Exports.Failed + Invitations.Failed + IdempotencyKeys.Failed;
}
