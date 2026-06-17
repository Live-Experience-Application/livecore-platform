// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Store;

/// <summary>
/// The generic, product-neutral kind of a store server notification that Core acts on (CORE-STORE-005, the
/// idempotent store notification handling story of the "Store Notifications" epic). A store
/// (<see cref="PurchaseProvider"/>) sends server-to-server notifications as a subscription's lifecycle evolves;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md ("Receipt verification" step 7) requires that "Store server
/// notifications update entitlement state on renewals, cancellations, refunds and grace periods". This enum is
/// exactly those four ACTIONABLE kinds — the ones that drive a <see cref="PurchaseTransaction"/> lifecycle
/// transition.
///
/// PROVIDER-NEUTRAL. A real store emits many raw notification types (Apple's <c>DID_RENEW</c> /
/// <c>DID_CHANGE_RENEWAL_STATUS</c> / <c>REFUND</c> / <c>GRACE_PERIOD_EXPIRED</c>; Google's
/// <c>SUBSCRIPTION_RENEWED</c> / <c>SUBSCRIPTION_CANCELED</c> / <c>SUBSCRIPTION_IN_GRACE_PERIOD</c>, etc.). The
/// deployment-supplied <see cref="IStoreNotificationParser"/> adapter maps a provider's raw type onto one of
/// these neutral kinds (or reports the notification as not actionable), so Core domain logic branches on this one
/// enum and never on an Apple- or Google-specific payload — the same isolation seam as the verification
/// abstraction (CORE-STORE-001).
///
/// Each kind maps to the single <see cref="PurchaseTransactionStatus"/> a notification of that kind drives a
/// purchase to (<see cref="StoreNotificationTypeExtensions.ToTransactionStatus"/>): a renewal keeps the purchase
/// <see cref="PurchaseTransactionStatus.Active"/> (entitlement retained), a cancellation moves it to
/// <see cref="PurchaseTransactionStatus.Cancelled"/>, a refund to <see cref="PurchaseTransactionStatus.Refunded"/>
/// and a grace period to <see cref="PurchaseTransactionStatus.InGracePeriod"/>.
/// The status change is itself audited and idempotent (CORE-STORE-002's <see cref="PurchaseTransactionService"/>),
/// so "Refunds and chargebacks must revoke or downgrade entitlements" and "Grace periods must be represented
/// explicitly" (docs/21 "Security requirements") hold by construction.
///
/// Persisted by its stable NAME on <c>store_notification_events.notification_type</c> via
/// <c>HasConversion&lt;string&gt;</c>, like every other enum in the model, so the integers below are only
/// in-memory discriminators with no ordering meaning and must not be compared with &gt;/&lt;.
/// </summary>
public enum StoreNotificationType
{
    /// <summary>
    /// A subscription renewed (a successful billing renewal). It keeps the purchase
    /// <see cref="PurchaseTransactionStatus.Active"/> — and, when the purchase was in a grace period, brings it
    /// back to active.
    /// </summary>
    Renewed = 1,

    /// <summary>
    /// A subscription was cancelled or will not renew. It moves the purchase to
    /// <see cref="PurchaseTransactionStatus.Cancelled"/> — the downgrade a cancellation causes (docs/21).
    /// </summary>
    Cancelled = 2,

    /// <summary>
    /// A purchase was refunded or charged back. It moves the purchase to
    /// <see cref="PurchaseTransactionStatus.Refunded"/> — the revocation a refund must cause ("Refunds and
    /// chargebacks must revoke or downgrade entitlements", docs/21).
    /// </summary>
    Refunded = 3,

    /// <summary>
    /// A renewal failed but the entitlement is retained for a billing-retry window. It moves the purchase to the
    /// explicit <see cref="PurchaseTransactionStatus.InGracePeriod"/> state ("Grace periods must be represented
    /// explicitly", docs/21).
    /// </summary>
    GracePeriodStarted = 4,
}

/// <summary>
/// Maps a <see cref="StoreNotificationType"/> to the lifecycle effect it has on a purchase, keeping the single
/// notification-kind → status mapping in one place so the handler and the persisted ledger can never disagree.
/// </summary>
public static class StoreNotificationTypeExtensions
{
    /// <summary>
    /// The single <see cref="PurchaseTransactionStatus"/> a notification of this kind drives a purchase to.
    /// </summary>
    /// <param name="type">The actionable notification kind.</param>
    /// <returns>The target purchase status.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The notification type is not a defined value.</exception>
    public static PurchaseTransactionStatus ToTransactionStatus(this StoreNotificationType type)
        => type switch
        {
            StoreNotificationType.Renewed => PurchaseTransactionStatus.Active,
            StoreNotificationType.Cancelled => PurchaseTransactionStatus.Cancelled,
            StoreNotificationType.Refunded => PurchaseTransactionStatus.Refunded,
            StoreNotificationType.GracePeriodStarted => PurchaseTransactionStatus.InGracePeriod,
            _ => throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Notification type is not a defined store notification type."),
        };
}
