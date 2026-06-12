namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of persisting a new <see cref="PurchaseTransaction"/> (CORE-STORE-002).
///
/// A verified purchase is named idempotently by the (provider, provider transaction id) pair (the unique
/// <c>purchase_transactions(provider, provider_transaction_id)</c> index), so an insert can race or repeat an
/// already-recorded purchase; this enum mirrors <see cref="SystemModule.IdempotencyKeyAddResult"/> /
/// <c>SubjectEntitlementAddResult</c>: a success value and an explicit duplicate outcome. The recording service
/// treats the duplicate outcome as "already recorded" (idempotent), so a client retry or replayed proof never
/// creates a second transaction. Any other failure surfaces as a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather than as a result
/// value.
/// </summary>
public enum PurchaseTransactionAddResult
{
    /// <summary>The purchase transaction was persisted.</summary>
    Added = 1,

    /// <summary>A transaction for the same (provider, provider transaction id) already exists; the insert was rejected.</summary>
    DuplicateTransaction = 2,
}
