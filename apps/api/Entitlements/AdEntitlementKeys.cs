namespace LiveCore.Api.Entitlements;

/// <summary>
/// The generic entitlement KEYS that decide a subject's ad eligibility (CORE-ADS-001, the ad eligibility story of
/// the "Ad Eligibility" epic). Each constant is a canonical, lower-case dotted entitlement key from the generic key
/// list in docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md / csv/mobile_entitlement_catalog.csv — product-neutral
/// Core vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv). Core decides whether a subject is eligible to see
/// ads from these entitlements; it never renders, requests, configures or places ads
/// (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md). A vertical maps each key to its own paywall copy in its UI and
/// never sees these names.
///
/// These keys are flags (<see cref="EntitlementValueKind.Flag"/>) resolved from a subject's ACTIVE entitlements by
/// <see cref="SubjectEntitlementResolver"/>; the deployment grants them through a <see cref="SubjectEntitlement"/>
/// after a verified purchase. The <see cref="AdEligibilityPolicy"/> reads them fail-closed: an absent or revoked
/// grant is simply "not entitled", so a client can never obtain ad-free state the server did not grant
/// ("Never trust client-side premium flags").
/// </summary>
public static class AdEntitlementKeys
{
    /// <summary>
    /// The explicit FREE-tier marker that the subject is on an ad-bearing plan (docs/21 generic key
    /// <c>ads.required</c>; "Server source of truth for ad eligibility"). It is an explicit server statement that
    /// the subject sees ads; the personal ad-free grant <see cref="AdsDisabled"/> overrides it. Its absence does not
    /// imply ad-free — the fail-closed default is still "ads required" (see <see cref="AdEligibilityPolicy"/>).
    /// </summary>
    public const string AdsRequired = "ads.required";

    /// <summary>
    /// The personal AD-FREE state (docs/21 generic key <c>ads.disabled</c>; "Personal ad-free state"). When the
    /// subject holds this flag granted <see langword="true"/>, ads are not required for them. This is the single
    /// server-authoritative signal that turns ads off for a subject (granted after a verified purchase); fail-closed,
    /// its absence means ads are required.
    /// </summary>
    public const string AdsDisabled = "ads.disabled";

    /// <summary>
    /// The HOSTED-SESSION ad-free capability (csv/mobile_entitlement_catalog.csv generic key
    /// <c>hosted.sessions.ads.disabled</c>; "removes ads for participants in hosted sessions"). When a host subject
    /// holds this flag granted <see langword="true"/>, the sessions they host are ad-free for participants. It is an
    /// INDEPENDENT capability surfaced to the client and does not change the subject's own
    /// <see cref="AdEligibilityResult.AdsRequired"/>. The catalog's dotted key conforms to the Core entitlement key
    /// shape (<see cref="EntitlementDefinition.IsValidKey"/>, which is dot-separated and has no underscores).
    /// </summary>
    public const string HostedSessionsAdsDisabled = "hosted.sessions.ads.disabled";
}
