namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of changing a purchase transaction's status through
/// <see cref="PurchaseTransactionService.ChangeStatusAsync"/> (CORE-STORE-002).
/// </summary>
public enum PurchaseStatusChangeOutcome
{
    /// <summary>The status moved to a new value; the change was persisted and recorded as a purchase event.</summary>
    Changed = 1,

    /// <summary>
    /// The transaction was already in the requested status; nothing changed and no audit event was written (the
    /// idempotent no-op path, so a duplicate store notification is safe).
    /// </summary>
    Unchanged = 2,

    /// <summary>No transaction exists for the given (provider, provider transaction id); nothing changed (fail-closed).</summary>
    TransactionNotFound = 3,
}
