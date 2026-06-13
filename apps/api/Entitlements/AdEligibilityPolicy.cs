namespace LiveCore.Api.Entitlements;

/// <summary>
/// The server-side decision of whether a subject is eligible to see ads (CORE-ADS-001) — one of the two Core-owned
/// ad types of docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md (alongside <see cref="AdEligibilityResult"/>). It is a
/// PURE function of a subject's resolved <see cref="EffectiveEntitlements"/>: it reuses the central
/// <see cref="SubjectEntitlementResolver"/> output (visibility/entitlement logic is not duplicated) and computes the
/// <see cref="AdEligibilityResult"/> with no I/O, so the "entitlement-driven ad eligibility" decision is trivially
/// testable and identical wherever it is consulted.
///
/// FAIL-CLOSED. Ads are required by default; ad-free state is granted only by an explicit, active server
/// entitlement, so a subject with no entitlements — or only a client-asserted one the server never recorded — sees
/// ads ("Never trust client-side premium flags"; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The policy
/// decides eligibility ONLY; it knows nothing about ad placements, providers, SDKs or unit ids (the epic acceptance
/// criterion: "Core returns ad eligibility without knowing ad placements").
///
/// Decision (from the generic flags in <see cref="AdEntitlementKeys"/>):
/// <list type="bullet">
///   <item>The subject holds <see cref="AdEntitlementKeys.AdsDisabled"/> = <see langword="true"/> (the personal
///   ad-free grant) ⇒ ads NOT required (<see cref="AdEligibilityReason.AdFreeEntitlement"/>). This grant overrides
///   the ad-bearing marker, so a contradictory grant still resolves to ad-free.</item>
///   <item>Otherwise the subject holds <see cref="AdEntitlementKeys.AdsRequired"/> = <see langword="true"/> (the
///   explicit ad-bearing plan marker) ⇒ ads required
///   (<see cref="AdEligibilityReason.AdsRequiredEntitlement"/>).</item>
///   <item>Otherwise (no relevant grant) ⇒ ads required, the fail-closed default
///   (<see cref="AdEligibilityReason.NoAdFreeEntitlement"/>).</item>
/// </list>
/// The hosted-session ad-free capability (<see cref="AdEntitlementKeys.HostedSessionsAdsDisabled"/>) is reported
/// independently and never changes the subject's own <see cref="AdEligibilityResult.AdsRequired"/>.
/// </summary>
public static class AdEligibilityPolicy
{
    /// <summary>
    /// Decides the subject's ad eligibility from its resolved effective entitlements. The set is the
    /// server-authoritative premium state (active grants only), so an absent or revoked flag is fail-closed to "not
    /// entitled".
    /// </summary>
    /// <param name="entitlements">The subject's resolved effective entitlements (never <see langword="null"/>).</param>
    public static AdEligibilityResult Evaluate(EffectiveEntitlements entitlements)
    {
        ArgumentNullException.ThrowIfNull(entitlements);

        // The personal ad-free grant is the single server signal that turns ads off; it overrides the ad-bearing
        // marker so a contradictory grant still resolves to the (paid) ad-free state.
        var personalAdFree = entitlements.IsFlagEnabled(AdEntitlementKeys.AdsDisabled);

        // The hosted-session ad-free capability is independent of the subject's own eligibility.
        var hostedSessionAdFree = entitlements.IsFlagEnabled(AdEntitlementKeys.HostedSessionsAdsDisabled);

        var (adsRequired, reason) = personalAdFree
            ? (false, AdEligibilityReason.AdFreeEntitlement)
            : entitlements.IsFlagEnabled(AdEntitlementKeys.AdsRequired)
                ? (true, AdEligibilityReason.AdsRequiredEntitlement)
                : (true, AdEligibilityReason.NoAdFreeEntitlement);

        // Core has no persisted temporary per-session ad-free grant yet (a future, mobile-driven rewarded-ad
        // window; docs/22), so there is never a session ad-free expiry to report.
        return new AdEligibilityResult(adsRequired, reason, SessionAdFreeUntil: null, hostedSessionAdFree);
    }
}
