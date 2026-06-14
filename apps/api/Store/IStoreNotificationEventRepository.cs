namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the append-only store notification ledger (CORE-STORE-005). The Store module owns the
/// <c>store_notification_events</c> table; other modules access it only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// A notification is addressed by its idempotency key — the (<see cref="PurchaseProvider"/>, provider
/// notification id) pair — so the lookup is by that pair (the dedup check), never a list-everything read. The
/// contract is APPEND-ONLY: it exposes only an idempotency lookup and an insert; there is NO update and NO delete,
/// because a recorded notification is an immutable fact (mirrors <see cref="IPurchaseEventRepository"/>).
/// </summary>
public interface IStoreNotificationEventRepository
{
    /// <summary>
    /// Finds the store notification event for exactly the given (provider, provider notification id) pair, or
    /// <see langword="null"/> when none exists. This is the idempotency lookup: it recognizes a notification that
    /// was already handled.
    /// </summary>
    /// <exception cref="System.ArgumentException">The provider notification id is blank.</exception>
    Task<StoreNotificationEvent?> FindByProviderNotificationAsync(
        PurchaseProvider provider,
        string providerNotificationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every recorded notification for a purchase — all rows for the given (provider, provider transaction id)
    /// pair — or an empty list when the purchase has no recorded notifications. This is the reconciliation
    /// re-derivation lookup (CORE-JOB-003 / CORE-MON-004): the purchase's converged status is the MONOTONIC FOLD of
    /// these notifications' <see cref="StoreNotificationEvent.AppliedStatus"/> values in event-time order
    /// (<see cref="PurchaseTransactionStatusMachine.Converge"/>), regardless of the order the notifications were
    /// delivered or applied — so a refund stays revoked even if a later renewal was recorded after it.
    /// </summary>
    /// <exception cref="System.ArgumentException">The provider transaction id is blank.</exception>
    Task<IReadOnlyList<StoreNotificationEvent>> ListByProviderTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new store notification event. A delivered notification is handled at most once, so an insert
    /// that duplicates an existing (provider, provider notification id) is reported as
    /// <see cref="StoreNotificationEventAddResult.DuplicateNotification"/> (the idempotency guarantee); any other
    /// failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<StoreNotificationEventAddResult> AddAsync(StoreNotificationEvent notificationEvent, CancellationToken cancellationToken);
}
