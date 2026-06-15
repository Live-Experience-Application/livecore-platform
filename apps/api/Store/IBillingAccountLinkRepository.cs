using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Store;

/// <summary>
/// Persistence contract for the buyer-linkage aggregate (CORE-MON-002). The Store module owns the
/// <c>billing_account_links</c> table; other modules access buyer links only through this contract or the
/// module's application services (docs/02_ARCHITECTURE.md: module boundaries).
///
/// A link is addressed by the purchase it binds (the <c>purchase_transaction_id</c>), which is unique per row, so
/// the per-purchase lookup is by that id — never a list-everything read of all links. The cross-product
/// entitlement-retention check (CORE-MON-012) also needs the reverse direction — all of ONE subject's linked
/// purchases — so it is a SUBJECT-scoped read (<see cref="ListLinkedPurchasesBySubjectAsync"/>), bounded by the
/// subject pair the same way <c>subject_entitlements</c> is read by subject.
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

    /// <summary>
    /// Lists every verified purchase linked to exactly the given subject — the reverse, subject-scoped read the
    /// cross-product entitlement-retention check needs (CORE-MON-012). Each result joins one link to the
    /// <see cref="PurchaseTransaction"/> it binds and carries the purchase id, its product reference and its current
    /// status, so the revocation service can exclude the purchase being revoked, keep only the still-active
    /// (non-revoked) purchases and resolve which entitlements those purchases still grant. Bounded by the
    /// (<paramref name="subjectType"/>, <paramref name="subjectId"/>) pair — one subject's purchases are never read
    /// through another subject's id (subject isolation, threat T5). Returns an empty list when the subject has no
    /// linked purchases.
    /// </summary>
    /// <exception cref="System.ArgumentException">The subject id is empty.</exception>
    Task<IReadOnlyList<LinkedSubjectPurchase>> ListLinkedPurchasesBySubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken);
}
