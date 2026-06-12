namespace LiveCore.Api.Store;

/// <summary>
/// The outcome of verifying a purchase proof against a store's server APIs (CORE-STORE-001), as reported by a
/// <see cref="PurchaseVerificationResult"/>. Verification is the gate that decides whether a submitted proof is
/// a genuine purchase before any entitlement is granted (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md
/// "Never unlock limits before server verification succeeds").
///
/// This is the binary verdict only — verified or not. It is NOT a network/availability signal: a provider being
/// unreachable or unconfigured is a fail-closed EXCEPTION (<see cref="PurchaseProviderNotConfiguredException"/>
/// for "no adapter"; a provider adapter's own transport error otherwise), never a silent
/// <see cref="Rejected"/>, so a transient outage can never be mistaken for a definitive "not a real purchase".
///
/// Persisted by name in later stories, so the integers below are only in-memory discriminators with no ordering
/// meaning.
/// </summary>
public enum PurchaseVerificationStatus
{
    /// <summary>
    /// The store confirmed the proof is a genuine purchase. The <see cref="PurchaseVerificationResult"/> carries
    /// the normalized <see cref="VerifiedPurchase"/>; downstream entitlement granting may proceed.
    /// </summary>
    Verified = 1,

    /// <summary>
    /// The store determined the proof is NOT a genuine, grantable purchase (invalid, forged, already consumed,
    /// or otherwise refused). No entitlement is granted (fail-closed). The result carries a generic, log-safe
    /// reason and no <see cref="VerifiedPurchase"/>.
    /// </summary>
    Rejected = 2,
}
