namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the verified purchase transaction aggregate (CORE-STORE-002). The Store module owns
/// the <c>purchase_transactions</c> table; other modules access purchase transactions only through this contract
/// or the module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// A purchase is addressed by its idempotency key — the (<see cref="PurchaseProvider"/>, provider transaction
/// id) pair — so the lookup is by that pair, never a list-everything read of all purchases.
/// </summary>
public interface IPurchaseTransactionRepository
{
    /// <summary>
    /// Persists a new purchase transaction. A verified purchase is recorded at most once, so an insert that
    /// duplicates an existing (provider, provider transaction id) is reported as
    /// <see cref="PurchaseTransactionAddResult.DuplicateTransaction"/> (the idempotency guarantee); any other
    /// failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<PurchaseTransactionAddResult> AddAsync(PurchaseTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Persists changes to an already-tracked transaction (a status change).</summary>
    Task UpdateAsync(PurchaseTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the purchase transaction for exactly the given (provider, provider transaction id) pair, or
    /// <see langword="null"/> when none exists. This is the idempotency lookup: it recognizes a purchase that was
    /// already recorded.
    /// </summary>
    /// <exception cref="System.ArgumentException">The provider transaction id is blank.</exception>
    Task<PurchaseTransaction?> FindByProviderTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken);
}
