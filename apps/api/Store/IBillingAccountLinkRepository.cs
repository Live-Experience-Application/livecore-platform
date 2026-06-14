namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the buyer-linkage aggregate (CORE-MON-002). The Store module owns the
/// <c>billing_account_links</c> table; other modules access buyer links only through this contract or the
/// module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// A link is addressed by the purchase it binds (the <c>purchase_transaction_id</c>), which is unique per row, so
/// the lookup is by that id — never a list-everything read of all links.
/// </summary>
public interface IBillingAccountLinkRepository
{
    /// <summary>
    /// Persists a new billing account link. A receipt maps to exactly one subject, so an insert that duplicates an
    /// existing <c>purchase_transaction_id</c> is reported as
    /// <see cref="BillingAccountLinkAddResult.DuplicatePurchase"/> (the one-subject-per-receipt guarantee); any
    /// other failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<BillingAccountLinkAddResult> AddAsync(BillingAccountLink link, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the billing account link for exactly the given purchase transaction, or <see langword="null"/> when
    /// the purchase is not yet linked. This is the ownership lookup: it recognizes a purchase that was already
    /// claimed by a subject.
    /// </summary>
    /// <exception cref="System.ArgumentException">The purchase transaction id is empty.</exception>
    Task<BillingAccountLink?> FindByPurchaseTransactionAsync(
        Guid purchaseTransactionId,
        CancellationToken cancellationToken);
}
