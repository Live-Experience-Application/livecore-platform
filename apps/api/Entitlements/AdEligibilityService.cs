// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Resolves a subject's ad eligibility server-side (CORE-ADS-001) — the thin application service that turns a
/// (subject type, subject id) into an <see cref="AdEligibilityResult"/>. It composes the central
/// <see cref="SubjectEntitlementResolver"/> (the single path that produces a subject's effective entitlements) with
/// the pure <see cref="AdEligibilityPolicy"/>, so the endpoint keeps no domain logic (docs/02_ARCHITECTURE.md:
/// "domain logic is not placed in controllers") and the ad decision is computed entirely server-side from server
/// entitlements (the epic acceptance criterion; docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md).
///
/// It is keyed purely by the subject; it does NOT authorize the caller. The endpoint resolves and authorizes the
/// subject first (the authenticated user for <c>GET /api/v1/me/ad-eligibility</c>) and only then asks this service,
/// exactly as <see cref="QuotaStatusCalculator"/> sits behind the quota-status endpoints.
/// </summary>
public sealed class AdEligibilityService
{
    private readonly SubjectEntitlementResolver _resolver;

    public AdEligibilityService(SubjectEntitlementResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <summary>
    /// Resolves the subject's active entitlements and decides its ad eligibility. A subject with no active
    /// entitlements is fail-closed to "ads required" (<see cref="AdEligibilityPolicy"/>).
    /// </summary>
    /// <param name="subjectType">The kind of subject to decide for.</param>
    /// <param name="subjectId">The subject's id (non-empty).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The subject id is empty.</exception>
    public async Task<AdEligibilityResult> EvaluateForSubjectAsync(
        EntitlementSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var entitlements = await _resolver
            .ResolveAsync(subjectType, subjectId, cancellationToken)
            .ConfigureAwait(false);

        return AdEligibilityPolicy.Evaluate(entitlements);
    }
}
