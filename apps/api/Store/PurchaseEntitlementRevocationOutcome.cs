// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of revoking the entitlement a purchase granted, when the purchase enters a revoked state
/// (CORE-MON-004, <see cref="PurchaseEntitlementRevocationService.RevokeForPurchaseAsync"/>). The revocation is the
/// inverse of the CORE-MON-003 grant chain, driven from a refund/cancellation/chargeback notification or its
/// reconciliation.
/// </summary>
public enum PurchaseEntitlementRevocationOutcome
{
    /// <summary>The purchase's buyer-linked entitlement (the mapped plan's grants) was revoked from the subject.</summary>
    Revoked = 1,

    /// <summary>
    /// No purchase exists for the given (provider, provider transaction id); nothing was revoked (fail-closed —
    /// nothing is fabricated for an unrecorded purchase).
    /// </summary>
    PurchaseNotFound = 2,

    /// <summary>
    /// The purchase exists but is not linked to a buyer subject (no <c>billing_account_links</c> row), so there is
    /// no subject whose entitlement to revoke. A safe no-op.
    /// </summary>
    NotLinked = 3,

    /// <summary>
    /// The purchase's product reference maps to no plan, so there is no mapped entitlement to revoke (an ungoverned
    /// product was never granted premium). A safe no-op.
    /// </summary>
    ProductNotMapped = 4,
}
