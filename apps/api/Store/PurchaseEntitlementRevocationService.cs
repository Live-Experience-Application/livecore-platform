using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Store;

/// <summary>
/// Revokes the entitlement a purchase granted when the purchase enters a REVOKED state (CORE-MON-004, the
/// monotonic-purchase-status story of the "Monetization v1" epic). It is the inverse of the CORE-MON-003 grant
/// chain and the story's "on entering a revoked state revoke the granted entitlement" requirement: given a purchase
/// that a refund, cancellation or chargeback drove to <see cref="PurchaseTransactionStatus.Refunded"/> /
/// <see cref="PurchaseTransactionStatus.Cancelled"/>, it resolves the buyer subject the purchase is linked to and
/// revokes the entitlements the purchase's product maps to ("Refunds and chargebacks must revoke or downgrade
/// entitlements", docs/21).
///
/// IT ONLY WIRES, IT DOES NOT DUPLICATE. The purchase → buyer linkage already exists
/// (<see cref="BillingAccountLink"/>, CORE-MON-002) and the product → plan → entitlement revoke already exists
/// (<see cref="ProductEntitlementGrantService.RevokeForProductAsync"/>, which reuses the CORE-ENTL-002 revoke
/// primitive); this service supplies only the remaining "purchase → its buyer + product" resolution that turns a
/// purchase-status change into the right subject's revocation. It mirrors how the Apple/Google verification
/// endpoints (CORE-MON-003) resolve the buyer and grant — here the trigger is the store-notification handler and
/// its reconciliation, which call this after driving the purchase into a revoked state.
///
/// IDEMPOTENT AND FAIL-CLOSED. A purchase Core never recorded revokes nothing
/// (<see cref="PurchaseEntitlementRevocationOutcome.PurchaseNotFound"/>); a purchase with no buyer link revokes
/// nothing (<see cref="PurchaseEntitlementRevocationOutcome.NotLinked"/>); a product that maps to no plan revokes
/// nothing (<see cref="PurchaseEntitlementRevocationOutcome.ProductNotMapped"/>). Revoking an already-revoked or
/// never-held entitlement is a safe no-op, so it is invoked unconditionally whenever a revoking notification lands
/// on an existing purchase (a re-delivery, a retry or a reconciliation sweep converges rather than erroring).
/// </summary>
public sealed class PurchaseEntitlementRevocationService
{
    private readonly IPurchaseTransactionRepository _transactions;
    private readonly IBillingAccountLinkRepository _links;
    private readonly ProductEntitlementGrantService _grants;

    public PurchaseEntitlementRevocationService(
        IPurchaseTransactionRepository transactions,
        IBillingAccountLinkRepository links,
        ProductEntitlementGrantService grants)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(grants);
        _transactions = transactions;
        _links = links;
        _grants = grants;
    }

    /// <summary>
    /// Revokes the entitlement the named purchase granted to its buyer. Looks the purchase up by its (provider,
    /// provider transaction id) idempotency key, resolves the buyer subject from the purchase's
    /// <see cref="BillingAccountLink"/>, and revokes the entitlements the purchase's product maps to (reusing
    /// <see cref="ProductEntitlementGrantService.RevokeForProductAsync"/>). Fail-closed: an unrecorded purchase, a
    /// purchase with no buyer link, or a product that maps to no plan revokes nothing. Idempotent.
    /// </summary>
    /// <param name="provider">The store that issued the purchase.</param>
    /// <param name="providerTransactionId">The provider-assigned transaction id naming the purchase.</param>
    /// <param name="revokedAt">When the revocation occurs (the authoritative notification's event time).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The provider transaction id is blank.</exception>
    public async Task<PurchaseEntitlementRevocationOutcome> RevokeForPurchaseAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken)
    {
        var transaction = await _transactions
            .FindByProviderTransactionAsync(provider, providerTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (transaction is null)
        {
            // Nothing is fabricated for a purchase Core never recorded.
            return PurchaseEntitlementRevocationOutcome.PurchaseNotFound;
        }

        var link = await _links
            .FindByPurchaseTransactionAsync(transaction.Id, cancellationToken)
            .ConfigureAwait(false);
        if (link is null)
        {
            // The purchase is recorded but not linked to a buyer subject, so there is no entitlement to revoke.
            return PurchaseEntitlementRevocationOutcome.NotLinked;
        }

        var revocation = await _grants
            .RevokeForProductAsync(link.SubjectType, link.SubjectId, transaction.ProductReference, revokedAt, cancellationToken)
            .ConfigureAwait(false);

        return revocation.Outcome == ProductEntitlementRevocationOutcome.ProductNotMapped
            ? PurchaseEntitlementRevocationOutcome.ProductNotMapped
            : PurchaseEntitlementRevocationOutcome.Revoked;
    }
}
