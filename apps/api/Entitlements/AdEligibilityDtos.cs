namespace LiveCore.Api.Entitlements;

/// <summary>
/// Client-safe response envelope of the ad-eligibility endpoint (CORE-ADS-001;
/// csv/mobile_store_api_routes.csv: <c>GET /v1/me/ad-eligibility</c>). It carries only the generic, server-decided
/// ad eligibility facts and matches the documented contract shape exactly
/// (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md):
/// <code>
/// { "adsRequired": true, "reason": "NO_AD_FREE_ENTITLEMENT", "sessionAdFreeUntil": null, "hostedSessionAdFree": false }
/// </code>
/// It never includes a subject id, an entitlement key, an ad placement, an ad provider/unit id or any SDK
/// configuration (the epic acceptance criterion "Core returns ad eligibility without knowing ad placements"; the
/// docs/22 "Forbidden" list; threat T7). A vertical maps the generic <see cref="Reason"/> code to its own paywall
/// copy and owns all ad rendering.
/// </summary>
/// <param name="AdsRequired">Whether the subject must be shown ads (fail-closed default <see langword="true"/>).</param>
/// <param name="Reason">The stable, generic wire code explaining the decision (e.g. <c>NO_AD_FREE_ENTITLEMENT</c>).</param>
/// <param name="SessionAdFreeUntil">The temporary per-session ad-free expiry, or <see langword="null"/> when none (currently always null).</param>
/// <param name="HostedSessionAdFree">Whether sessions hosted by the subject are ad-free for participants.</param>
public sealed record AdEligibilityResponse(
    bool AdsRequired,
    string Reason,
    DateTimeOffset? SessionAdFreeUntil,
    bool HostedSessionAdFree)
{
    /// <summary>Projects a decided <see cref="AdEligibilityResult"/> into the client-safe response envelope.</summary>
    public static AdEligibilityResponse From(AdEligibilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AdEligibilityResponse(
            result.AdsRequired,
            result.Reason.ToWireCode(),
            result.SessionAdFreeUntil,
            result.HostedSessionAdFree);
    }
}
