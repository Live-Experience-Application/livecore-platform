// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// A purchase the reconciliation sweep should re-derive (CORE-JOB-003): the (provider, provider transaction id)
/// pair that names a persisted <see cref="PurchaseTransaction"/> whose current status differs from the status its
/// latest-by-event-time store notification implies (a missed or out-of-order delivery). It carries only the
/// idempotency-key identifiers needed to reconcile the purchase — no buyer, no tenant, no secret — exactly the
/// stable identifiers <see cref="StoreNotificationService.ReconcileTransactionAsync"/> takes.
/// </summary>
/// <param name="Provider">The store that issued the purchase.</param>
/// <param name="ProviderTransactionId">The provider-assigned transaction id naming the purchase.</param>
public readonly record struct ReconcilablePurchase(PurchaseProvider Provider, string ProviderTransactionId);
