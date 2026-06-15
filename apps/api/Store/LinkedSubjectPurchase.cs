namespace LiveCore.Api.Store;

/// <summary>
/// A verified purchase linked to a subject, projected for the cross-product entitlement-retention check
/// (CORE-MON-012, the "Narrow cross-product entitlement over-revocation" story of the "Monetization v1" epic). It
/// joins one <see cref="BillingAccountLink"/> to the <see cref="PurchaseTransaction"/> it binds, carrying just the
/// three facts the retention decision needs: the purchase's id (so the purchase being revoked can be excluded), its
/// current <see cref="Status"/> (so a still-active, non-revoked purchase can be distinguished from a revoked one via
/// <see cref="PurchaseTransactionStatusMachine.IsRevoked"/>) and its <see cref="ProductReference"/> (so the
/// entitlements that purchase still grants can be resolved through the product → plan mapping).
///
/// It carries only stable identifiers and the lifecycle enum — no buyer id beyond the queried subject, no tenant and
/// no secret — exactly the shape <see cref="PurchaseEntitlementRevocationService"/> needs to decide whether a shared
/// entitlement is still granted by another active purchase of the same subject and so must be retained.
/// </summary>
/// <param name="PurchaseTransactionId">The id of the linked purchase (used to exclude the purchase being revoked).</param>
/// <param name="ProductReference">The opaque store product reference the purchase is for (maps to a plan).</param>
/// <param name="Status">The purchase's current lifecycle status (an active purchase is one that is not revoked).</param>
public sealed record LinkedSubjectPurchase(
    Guid PurchaseTransactionId,
    string ProductReference,
    PurchaseTransactionStatus Status);
