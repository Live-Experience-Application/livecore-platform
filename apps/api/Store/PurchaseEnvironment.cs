// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// The store ENVIRONMENT a purchase proof was verified against (CORE-MON-008) — the sandbox/test environment a
/// provider exposes for development, or the live production environment real money flows through. Both Apple and
/// Google issue receipts in distinct environments (Apple's signed transaction carries an <c>environment</c> of
/// <c>Sandbox</c>/<c>Production</c>; Google distinguishes test purchases from live ones), and a deployment-supplied
/// <see cref="IPurchaseVerificationProvider"/> adapter knows which environment it verified a proof in. This enum is
/// how the adapter REPORTS that on the normalized <see cref="VerifiedPurchase.Environment"/>, so Core can keep the
/// two apart without parsing any provider payload — the sandbox/production half of "behind a fail-closed port that
/// distinguishes sandbox vs production" (the story acceptance criterion).
///
/// WHY CORE CARES. A sandbox receipt is free to mint and trivially forgeable in the provider's test harness, so
/// honoring one on a production deployment would let anyone unlock premium state without paying — the bypass this
/// module exists to prevent ("Never trust client-side premium flags"; "Never unlock limits before server
/// verification succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). Core therefore gates honoring on the
/// reported environment through the fail-closed <see cref="PurchaseEnvironmentPolicy"/>: a production deployment
/// honors only a <see cref="Production"/> purchase and rejects a <see cref="Sandbox"/> one. The cryptographic
/// verification itself stays in the deployment-supplied adapter (out of Core per threat T7 /
/// docs/13_SELF_HOSTING_REQUIREMENTS.md); Core only guarantees the fail-closed separation around it.
///
/// Like every other enum in the model (e.g. <see cref="PurchaseProvider"/>, <see cref="PurchaseVerificationStatus"/>)
/// it is an in-memory discriminator with no ordering meaning; the integers below must not be compared with
/// &gt;/&lt;. It is not persisted — the honoring decision happens BEFORE a purchase is recorded, so a recorded
/// purchase is always one this deployment honors and no schema column is needed.
/// </summary>
public enum PurchaseEnvironment
{
    /// <summary>
    /// The provider's SANDBOX / test environment. Sandbox receipts are issued by the provider's test harness for
    /// development and are not real purchases; a production deployment must NOT honor one (it is the fail-closed
    /// default a conforming adapter reports only when it cannot determine the environment — see
    /// <see cref="VerifiedPurchase.Create"/>).
    /// </summary>
    Sandbox = 1,

    /// <summary>
    /// The provider's live PRODUCTION environment — a real, money-backed purchase. Honored by any deployment
    /// (a sandbox/test deployment may also honor production purchases for verification, but a production
    /// deployment honors ONLY this; see <see cref="PurchaseEnvironmentPolicy"/>).
    /// </summary>
    Production = 2,
}
