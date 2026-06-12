namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of recording a verified purchase through
/// <see cref="PurchaseTransactionService.RecordVerifiedPurchaseAsync"/> (CORE-STORE-002).
/// </summary>
public enum PurchaseRecordingOutcome
{
    /// <summary>The verified purchase was persisted as a new transaction, and its initial state was audited.</summary>
    Recorded = 1,

    /// <summary>
    /// A transaction for the same (provider, provider transaction id) already existed; nothing was persisted and
    /// no duplicate audit event was written (the idempotent retry path).
    /// </summary>
    AlreadyRecorded = 2,
}

/// <summary>
/// Result of recording a verified purchase (CORE-STORE-002). Beyond the <see cref="Outcome"/> it carries the
/// persisted (or already-existing) <see cref="Transaction"/>, so a caller (a later verification endpoint) can
/// act on the recorded purchase — for example to grant the resulting entitlement — without a second lookup.
/// </summary>
/// <param name="Outcome">Whether the purchase was newly recorded or already existed.</param>
/// <param name="Transaction">The persisted or already-existing purchase transaction.</param>
public sealed record PurchaseRecordingResult(PurchaseRecordingOutcome Outcome, PurchaseTransaction Transaction);
