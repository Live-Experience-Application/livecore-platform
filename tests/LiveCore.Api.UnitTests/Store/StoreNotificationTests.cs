// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Store;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Unit tests for the store notification value types of CORE-STORE-005: the <see cref="StoreNotificationType"/> →
/// <see cref="PurchaseTransactionStatus"/> mapping (the single place a notification kind decides its lifecycle
/// effect), the normalized <see cref="StoreNotification"/>'s validation and log-safety, the
/// <see cref="StoreNotificationEnvelope"/> bounds, and the <see cref="StoreNotificationEvent"/> ledger guards.
/// These pin the product-neutral contract and the threat-T7 log-safety (no raw payload / receipt content).
/// </summary>
public sealed class StoreNotificationTests
{
    private static readonly DateTimeOffset _at = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(StoreNotificationType.Renewed, PurchaseTransactionStatus.Active)]
    [InlineData(StoreNotificationType.Cancelled, PurchaseTransactionStatus.Cancelled)]
    [InlineData(StoreNotificationType.Refunded, PurchaseTransactionStatus.Refunded)]
    [InlineData(StoreNotificationType.GracePeriodStarted, PurchaseTransactionStatus.InGracePeriod)]
    public void Each_notification_type_maps_to_its_lifecycle_status(
        StoreNotificationType type,
        PurchaseTransactionStatus expected)
        => Assert.Equal(expected, type.ToTransactionStatus());

    [Fact]
    public void Create_normalizes_and_trims_the_identifiers()
    {
        var notification = StoreNotification.Create(
            PurchaseProvider.Apple, "  ntf-1  ", StoreNotificationType.Refunded, "  txn-1  ", _at);

        Assert.Equal(PurchaseProvider.Apple, notification.Provider);
        Assert.Equal("ntf-1", notification.ProviderNotificationId);
        Assert.Equal("txn-1", notification.ProviderTransactionId);
        Assert.Equal(StoreNotificationType.Refunded, notification.Type);
        Assert.Equal(_at.ToUniversalTime(), notification.OccurredAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_notification_id(string? notificationId)
        => Assert.Throws<ArgumentException>(() =>
            StoreNotification.Create(PurchaseProvider.Apple, notificationId!, StoreNotificationType.Refunded, "txn-1", _at));

    [Fact]
    public void Create_rejects_an_undefined_provider()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            StoreNotification.Create((PurchaseProvider)999, "ntf-1", StoreNotificationType.Refunded, "txn-1", _at));

    [Fact]
    public void Create_rejects_a_defaulted_event_time()
        // A defaulted/zero OccurredAt (CORE-MON-004) would corrupt the event-time ordering reconciliation re-derives
        // a purchase's converged status from, so it is rejected fail-closed rather than normalized into the ledger.
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            StoreNotification.Create(PurchaseProvider.Apple, "ntf-1", StoreNotificationType.Refunded, "txn-1", default));

    [Fact]
    public void Create_rejects_an_absurdly_early_event_time()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            StoreNotification.Create(
                PurchaseProvider.Apple,
                "ntf-1",
                StoreNotificationType.Refunded,
                "txn-1",
                StoreNotification.MinOccurredAt.AddSeconds(-1)));

    [Fact]
    public void Create_accepts_an_event_time_on_the_minimum_bound()
    {
        var notification = StoreNotification.Create(
            PurchaseProvider.Apple, "ntf-1", StoreNotificationType.Refunded, "txn-1", StoreNotification.MinOccurredAt);

        Assert.Equal(StoreNotification.MinOccurredAt, notification.OccurredAt);
    }

    [Fact]
    public void Notification_to_string_carries_identifiers_but_no_payload()
    {
        var text = StoreNotification.Create(
            PurchaseProvider.Apple, "ntf-1", StoreNotificationType.Refunded, "txn-1", _at).ToString();

        Assert.Contains("ntf-1", text, StringComparison.Ordinal);
        Assert.Contains("txn-1", text, StringComparison.Ordinal);
        Assert.Contains(nameof(StoreNotificationType.Refunded), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_to_string_excludes_the_raw_payload()
    {
        // The raw payload is an unvalidated, possibly receipt-bearing blob and must never appear in logs (threat T7).
        const string secretPayload = "signed-receipt-secret-body";
        var envelope = StoreNotificationEnvelope.Create(PurchaseProvider.Apple, secretPayload);

        Assert.DoesNotContain(secretPayload, envelope.ToString(), StringComparison.Ordinal);
        Assert.Contains(nameof(PurchaseProvider.Apple), envelope.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Envelope_rejects_a_blank_payload(string? payload)
        => Assert.Throws<ArgumentException>(() => StoreNotificationEnvelope.Create(PurchaseProvider.Apple, payload!));

    [Fact]
    public void Envelope_rejects_an_oversize_payload()
    {
        var oversize = new string('x', StoreNotificationEnvelope.MaxRawPayloadLength + 1);
        Assert.Throws<ArgumentException>(() => StoreNotificationEnvelope.Create(PurchaseProvider.Apple, oversize));
    }

    [Fact]
    public void Recording_a_notification_event_is_self_describing_from_the_notification()
    {
        var notification = StoreNotification.Create(
            PurchaseProvider.Google, "ntf-7", StoreNotificationType.Cancelled, "order-7", _at);

        var row = StoreNotificationEvent.Record(notification, StoreNotificationProcessingOutcome.Applied, _at);

        Assert.Equal(PurchaseProvider.Google, row.Provider);
        Assert.Equal("ntf-7", row.ProviderNotificationId);
        Assert.Equal(StoreNotificationType.Cancelled, row.NotificationType);
        Assert.Equal("order-7", row.ProviderTransactionId);
        // The applied status is derived from the notification type, so the row states what the notification did.
        Assert.Equal(PurchaseTransactionStatus.Cancelled, row.AppliedStatus);
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, row.Outcome);
    }

    [Fact]
    public void Recording_a_notification_event_rejects_the_already_processed_outcome()
    {
        // AlreadyProcessed is the pre-persistence dedup short-circuit and must never be stored as a row's outcome.
        var notification = StoreNotification.Create(
            PurchaseProvider.Apple, "ntf-1", StoreNotificationType.Refunded, "txn-1", _at);

        Assert.Throws<ArgumentException>(() =>
            StoreNotificationEvent.Record(notification, StoreNotificationProcessingOutcome.AlreadyProcessed, _at));
    }
}
