namespace LiveCore.Api.Entitlements;

/// <summary>
/// The reason a subject's ad eligibility was decided the way it was (CORE-ADS-001) — the generic, product-neutral
/// explanation that accompanies <see cref="AdEligibilityResult.AdsRequired"/>. It is derived ENTIRELY from the
/// subject's server-side entitlements (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md), so a client can display why
/// ads are or are not required without re-deciding it client-side. The reason is a stable wire code
/// (<see cref="AdEligibilityReasonExtensions.ToWireCode"/>); a vertical maps it to its own paywall copy in its UI.
/// </summary>
public enum AdEligibilityReason
{
    /// <summary>
    /// The subject holds no ad-free entitlement, so ads are required. This is the FAIL-CLOSED default — an unknown or
    /// unentitled subject sees ads, because ad-free state is granted only by the server ("Never trust client-side
    /// premium flags"). Wire code <c>NO_AD_FREE_ENTITLEMENT</c>.
    /// </summary>
    NoAdFreeEntitlement = 1,

    /// <summary>
    /// The subject explicitly holds the ad-bearing plan marker (<see cref="AdEntitlementKeys.AdsRequired"/> granted
    /// <see langword="true"/>) and no overriding ad-free grant, so ads are required. Wire code
    /// <c>ADS_REQUIRED_ENTITLEMENT</c>.
    /// </summary>
    AdsRequiredEntitlement = 2,

    /// <summary>
    /// The subject holds the personal ad-free grant (<see cref="AdEntitlementKeys.AdsDisabled"/> granted
    /// <see langword="true"/>), so ads are not required. Wire code <c>AD_FREE_ENTITLEMENT</c>.
    /// </summary>
    AdFreeEntitlement = 3,
}

/// <summary>
/// Maps an <see cref="AdEligibilityReason"/> to its stable, client-facing wire code. The code is the value the
/// <c>GET /api/v1/me/ad-eligibility</c> response carries (docs/22 example: <c>"reason": "NO_AD_FREE_ENTITLEMENT"</c>),
/// kept separate from the enum identifier so the response contract is explicit and unaffected by a future rename.
/// </summary>
public static class AdEligibilityReasonExtensions
{
    /// <summary>The stable wire code for the reason.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The reason is not a defined value.</exception>
    public static string ToWireCode(this AdEligibilityReason reason) => reason switch
    {
        AdEligibilityReason.NoAdFreeEntitlement => "NO_AD_FREE_ENTITLEMENT",
        AdEligibilityReason.AdsRequiredEntitlement => "ADS_REQUIRED_ENTITLEMENT",
        AdEligibilityReason.AdFreeEntitlement => "AD_FREE_ENTITLEMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown ad eligibility reason."),
    };
}
