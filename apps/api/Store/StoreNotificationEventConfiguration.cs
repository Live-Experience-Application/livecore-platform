// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Store;

/// <summary>
/// Relational mapping of <see cref="StoreNotificationEvent"/> to the append-only <c>store_notification_events</c>
/// table (CORE-STORE-005; csv/database_tables.csv: module Store;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions": <c>store_notification_events</c>). A
/// row is one handled store notification: the <c>provider</c> and the store-assigned
/// <c>provider_notification_id</c> (its idempotency key), the actionable <c>notification_type</c>, the affected
/// purchase's <c>provider_transaction_id</c>, the <c>applied_status</c> the notification drove the purchase to,
/// the processing <c>outcome</c> and the <c>received_at</c> timestamp.
///
/// THE IDEMPOTENCY GUARANTEE. The single documented critical index is the UNIQUE composite index on
/// (<c>provider</c>, <c>provider_notification_id</c>): a store notification id is unique within its provider, so
/// the same delivered notification is handled only once — a re-delivered or replayed notification is rejected at
/// the database and surfaces as <see cref="StoreNotificationEventAddResult.DuplicateNotification"/> ("Store
/// notifications must be idempotent", docs/21), the same shape as the unique
/// <c>purchase_transactions(provider, provider_transaction_id)</c> index.
///
/// APPEND-ONLY (mirrors <c>audit_logs</c>, <c>session_events</c> and <c>purchase_events</c>). The repository
/// exposes only an insert and an idempotency lookup — no update, no delete — because a recorded notification is an
/// immutable fact.
///
/// NO FOREIGN KEY TO THE PURCHASE. <c>provider_transaction_id</c> is a recorded identifier, not a database foreign
/// key: a notification can legitimately arrive for a purchase Core never recorded (handled fail-closed as
/// <see cref="StoreNotificationProcessingOutcome.TransactionNotFound"/>), and the notification fact must survive
/// regardless — mirroring how <c>audit_logs</c> records references as facts rather than cascading foreign keys.
///
/// ENUMS AS STABLE NAMES, NO RAW PAYLOAD. <c>provider</c>, <c>notification_type</c>, <c>applied_status</c> and
/// <c>outcome</c> are persisted by their stable enum NAME via <c>HasConversion&lt;string&gt;</c> (mirrors
/// <c>purchase_transactions.status</c>); they are first-class string columns, never JSON. The raw notification
/// body is never stored — only the normalized identifiers — so no signed receipt content is persisted (threat T7).
/// </summary>
internal sealed class StoreNotificationEventConfiguration : IEntityTypeConfiguration<StoreNotificationEvent>
{
    public void Configure(EntityTypeBuilder<StoreNotificationEvent> builder)
    {
        builder.ToTable("store_notification_events");

        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(notification => notification.Provider)
            .HasColumnName("provider")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(notification => notification.ProviderNotificationId)
            .HasColumnName("provider_notification_id")
            .HasMaxLength(StoreNotificationEvent.MaxIdentifierLength)
            .IsRequired();

        builder.Property(notification => notification.NotificationType)
            .HasColumnName("notification_type")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(notification => notification.ProviderTransactionId)
            .HasColumnName("provider_transaction_id")
            .HasMaxLength(StoreNotificationEvent.MaxIdentifierLength)
            .IsRequired();

        builder.Property(notification => notification.AppliedStatus)
            .HasColumnName("applied_status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(notification => notification.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(notification => notification.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(notification => notification.ReceivedAt)
            .HasColumnName("received_at")
            .IsRequired();

        // Documented critical index store_notification_events(provider, provider_notification_id), UNIQUE: the
        // uniqueness IS the idempotency guarantee — the same delivered notification is handled only once.
        builder.HasIndex(notification => new { notification.Provider, notification.ProviderNotificationId })
            .HasDatabaseName("ix_store_notification_events_provider_provider_notification_id")
            .IsUnique();

        // Reconciliation lookup index (CORE-JOB-003): the reconciliation job reads a purchase's notifications by
        // (provider, provider_transaction_id) and takes the one with the latest occurred_at to re-derive the
        // converged status. This non-unique index serves both the per-transaction "latest notification" lookup and
        // the candidate scan, so a re-derivation never table-scans the ledger.
        builder.HasIndex(notification => new
        {
            notification.Provider,
            notification.ProviderTransactionId,
            notification.OccurredAt,
        })
            .HasDatabaseName("ix_store_notification_events_provider_transaction_occurred_at");
    }
}
