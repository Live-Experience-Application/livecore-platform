// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// A subject's resolved ad eligibility (CORE-ADS-001) — the server's decision about whether the subject is eligible
/// to see ads, produced by <see cref="AdEligibilityPolicy"/> from the subject's effective entitlements. It is one of
/// the two Core-owned ad types of docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md (alongside
/// <see cref="AdEligibilityPolicy"/>): "Core may decide whether a subject is eligible to see ads. Core must not
/// render, request, configure or display ads."
///
/// The result carries only the generic, entitlement-derived decision — no ad placements, no ad provider/unit
/// identifiers, no SDK configuration (the epic acceptance criterion: "Core returns ad eligibility without knowing ad
/// placements"). A vertical maps it to its own ad rendering and paywall UI; Core never learns where or how ads are
/// shown.
/// </summary>
/// <param name="AdsRequired">
/// Whether the subject must be shown ads. <see langword="true"/> is the FAIL-CLOSED default; it is
/// <see langword="false"/> only when the server granted the subject a personal ad-free entitlement
/// (<see cref="AdEntitlementKeys.AdsDisabled"/>).
/// </param>
/// <param name="Reason">The generic, entitlement-derived reason for the decision.</param>
/// <param name="SessionAdFreeUntil">
/// The instant until which a TEMPORARY, per-session ad-free window applies, or <see langword="null"/> when none is
/// in force. Core has no persisted temporary session ad-free grant yet (a rewarded-ad temporary window is a future
/// story and its timing/placement is mobile-owned, docs/22), so this is currently always <see langword="null"/>; the
/// field is part of the documented contract shape so a future grant can populate it without a contract change.
/// </param>
/// <param name="HostedSessionAdFree">
/// Whether sessions HOSTED by this subject are ad-free for their participants — the subject holds the hosted-session
/// ad-free capability (<see cref="AdEntitlementKeys.HostedSessionsAdsDisabled"/>). This is an independent capability
/// and does not change <paramref name="AdsRequired"/> (the subject's own ad eligibility).
/// </param>
public sealed record AdEligibilityResult(
    bool AdsRequired,
    AdEligibilityReason Reason,
    DateTimeOffset? SessionAdFreeUntil,
    bool HostedSessionAdFree);
