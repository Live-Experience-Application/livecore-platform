// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// Outcome of handling one store notification through <see cref="StoreNotificationService.HandleAsync"/>
/// (CORE-STORE-005). The first three values are the effect of applying the notification's status change to the
/// affected purchase and are PERSISTED on the <c>store_notification_events.outcome</c> column as the auditable
/// record of what the notification did; <see cref="AlreadyProcessed"/> is the pre-persistence idempotent dedup
/// short-circuit and is therefore NEVER stored (a duplicate writes no second ledger row).
/// </summary>
public enum StoreNotificationProcessingOutcome
{
    /// <summary>
    /// The notification moved the purchase to a new lifecycle status (a renewal reactivated it, or a
    /// cancellation/refund/grace period downgraded it); the change was persisted and audited as a purchase event.
    /// </summary>
    Applied = 1,

    /// <summary>
    /// The purchase was already in the status the notification implies; nothing changed and no purchase event was
    /// written (the underlying status change is itself idempotent, so a re-delivered-but-not-yet-deduplicated
    /// notification is still safe).
    /// </summary>
    Unchanged = 2,

    /// <summary>
    /// No purchase exists for the notification's (provider, provider transaction id); nothing changed
    /// (fail-closed — a notification for an unknown purchase can never fabricate one). The notification is still
    /// recorded so its arrival is auditable and not reprocessed.
    /// </summary>
    TransactionNotFound = 3,

    /// <summary>
    /// A notification with the same (provider, provider notification id) was already handled; it is recognized by
    /// the dedup ledger and ignored with no further effect (the idempotent retry path — "Store notifications must
    /// be idempotent", docs/21). Never persisted.
    /// </summary>
    AlreadyProcessed = 4,
}
