// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of persisting a new <see cref="BillingAccountLink"/> (CORE-MON-002).
///
/// A receipt maps to exactly one subject (the unique <c>billing_account_links(purchase_transaction_id)</c>
/// index), so an insert can race or repeat an already-linked purchase; this enum mirrors
/// <see cref="PurchaseTransactionAddResult"/> / <c>SubjectEntitlementAddResult</c>: a success value and an
/// explicit duplicate outcome. The linking service treats the duplicate outcome by re-reading the existing link
/// and deciding whether it belongs to the same buyer (idempotent) or a different one (fail-closed conflict), so a
/// client retry, a replayed proof or a concurrent claim by another subject never creates a second link. Any other
/// failure surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> from the repository rather
/// than as a result value.
/// </summary>
public enum BillingAccountLinkAddResult
{
    /// <summary>The billing account link was persisted.</summary>
    Added = 1,

    /// <summary>
    /// A link for the same <c>purchase_transaction_id</c> already exists; the insert was rejected by the unique
    /// index. The purchase is already linked to a subject (the same buyer on a retry, or a different one trying to
    /// claim the receipt).
    /// </summary>
    DuplicatePurchase = 2,
}
