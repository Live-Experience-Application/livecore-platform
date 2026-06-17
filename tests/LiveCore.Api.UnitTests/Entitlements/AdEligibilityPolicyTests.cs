// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.UnitTests.Entitlements;

/// <summary>
/// Unit tests for <see cref="AdEligibilityPolicy"/> (CORE-ADS-001) — the pure, entitlement-driven decision behind
/// "Core returns ad eligibility without knowing ad placements" (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md). These
/// are the story's required "entitlement-driven ad eligibility tests": they pin the POSITIVE decisions (a granted
/// ad-free flag turns ads off) and the NEGATIVE, FAIL-CLOSED defaults (no grant — or a client-asserted flag the
/// server never recorded — means ads are required), so a client can never obtain ad-free state the server did not
/// grant (docs/21 "Never trust client-side premium flags"). The policy is keyed only by the resolved entitlements,
/// so it is exercised directly with seeded <see cref="EffectiveEntitlements"/>.
/// </summary>
public class AdEligibilityPolicyTests
{
    private static readonly DateTimeOffset _grantedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _subjectId = Guid.CreateVersion7();
    private static readonly Guid _planId = Guid.CreateVersion7();

    private static SubjectEntitlement Flag(string key, bool value)
    {
        var definition = EntitlementDefinition.Define(key, EntitlementValueKind.Flag, "Flag", null, _grantedAt);
        return SubjectEntitlement.GrantFlag(EntitlementSubjectType.User, _subjectId, definition, value, _planId, _grantedAt);
    }

    private static AdEligibilityResult Evaluate(params SubjectEntitlement[] assignments)
        => AdEligibilityPolicy.Evaluate(EffectiveEntitlements.FromActiveAssignments(assignments));

    // --- Fail-closed default ----------------------------------------------------

    [Fact]
    public void No_entitlements_requires_ads_fail_closed()
    {
        // The crown jewel: a subject the server granted nothing sees ads.
        var result = AdEligibilityPolicy.Evaluate(EffectiveEntitlements.Empty);

        Assert.True(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.NoAdFreeEntitlement, result.Reason);
        Assert.Null(result.SessionAdFreeUntil);
        Assert.False(result.HostedSessionAdFree);
    }

    // --- Personal ad-free grant -------------------------------------------------

    [Fact]
    public void Ads_disabled_granted_true_makes_ads_not_required()
    {
        var result = Evaluate(Flag(AdEntitlementKeys.AdsDisabled, true));

        Assert.False(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.AdFreeEntitlement, result.Reason);
    }

    [Fact]
    public void Ads_disabled_granted_false_still_requires_ads()
    {
        // A flag the server granted as false is NOT an ad-free grant (fail-closed); with no ad-bearing marker the
        // reason is the "no ad-free entitlement" default.
        var result = Evaluate(Flag(AdEntitlementKeys.AdsDisabled, false));

        Assert.True(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.NoAdFreeEntitlement, result.Reason);
    }

    // --- Explicit ad-bearing marker ---------------------------------------------

    [Fact]
    public void Ads_required_granted_true_requires_ads_with_explicit_reason()
    {
        var result = Evaluate(Flag(AdEntitlementKeys.AdsRequired, true));

        Assert.True(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.AdsRequiredEntitlement, result.Reason);
    }

    [Fact]
    public void Ad_free_grant_overrides_the_ad_bearing_marker()
    {
        // A contradictory grant (both flags true) resolves to the paid ad-free state — ads.disabled wins.
        var result = Evaluate(
            Flag(AdEntitlementKeys.AdsRequired, true),
            Flag(AdEntitlementKeys.AdsDisabled, true));

        Assert.False(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.AdFreeEntitlement, result.Reason);
    }

    // --- Hosted-session capability (independent) ---------------------------------

    [Fact]
    public void Hosted_session_ad_free_is_reported_independently_of_own_eligibility()
    {
        // The host capability does not change the subject's OWN adsRequired.
        var result = Evaluate(Flag(AdEntitlementKeys.HostedSessionsAdsDisabled, true));

        Assert.True(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.NoAdFreeEntitlement, result.Reason);
        Assert.True(result.HostedSessionAdFree);
    }

    [Fact]
    public void Personal_and_hosted_ad_free_can_both_apply()
    {
        var result = Evaluate(
            Flag(AdEntitlementKeys.AdsDisabled, true),
            Flag(AdEntitlementKeys.HostedSessionsAdsDisabled, true));

        Assert.False(result.AdsRequired);
        Assert.Equal(AdEligibilityReason.AdFreeEntitlement, result.Reason);
        Assert.True(result.HostedSessionAdFree);
    }

    // --- Wire codes -------------------------------------------------------------

    [Theory]
    [InlineData(AdEligibilityReason.NoAdFreeEntitlement, "NO_AD_FREE_ENTITLEMENT")]
    [InlineData(AdEligibilityReason.AdsRequiredEntitlement, "ADS_REQUIRED_ENTITLEMENT")]
    [InlineData(AdEligibilityReason.AdFreeEntitlement, "AD_FREE_ENTITLEMENT")]
    public void Reason_maps_to_its_stable_wire_code(AdEligibilityReason reason, string expectedCode)
        => Assert.Equal(expectedCode, reason.ToWireCode());

    [Fact]
    public void Session_ad_free_until_is_always_null_for_now()
    {
        // No persisted temporary session ad-free grant exists yet (a future, mobile-driven rewarded-ad window).
        Assert.Null(Evaluate(Flag(AdEntitlementKeys.AdsDisabled, true)).SessionAdFreeUntil);
        Assert.Null(AdEligibilityPolicy.Evaluate(EffectiveEntitlements.Empty).SessionAdFreeUntil);
    }
}
