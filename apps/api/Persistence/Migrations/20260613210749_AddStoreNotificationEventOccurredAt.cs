using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the <c>occurred_at</c> column (and a supporting lookup index) to the Store module's append-only
/// <c>store_notification_events</c> table: the store's reported event time of each handled notification, needed by
/// the store-notification reconciliation job (CORE-JOB-003, the "Worker Background Jobs" epic;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The synchronous webhook (CORE-STORE-005) applies
/// notifications in DELIVERY order, but a store delivers at least once and can reorder or drop deliveries, so the
/// reconciliation job re-derives a purchase's converged status from the notification with the latest
/// <c>occurred_at</c> rather than the one received last. <c>received_at</c> (the delivery time) already existed but
/// never reflects the store's true event ordering, so it cannot serve as the ordering key.
///
/// BACK-FILL FROM <c>received_at</c>. The column is NOT NULL. Existing rows are back-filled from their
/// <c>received_at</c> — the most faithful proxy for a historical notification's event time — rather than a sentinel
/// minimum, so any pre-existing ledger row keeps a meaningful, ordered timestamp. The column is added nullable, the
/// existing rows are back-filled, then the column is made NOT NULL; new rows always carry the parser-supplied
/// event time, so the back-fill only governs rows written before this migration.
///
/// LOOKUP INDEX. The non-unique index <c>ix_store_notification_events_provider_transaction_occurred_at</c> on
/// (<c>provider</c>, <c>provider_transaction_id</c>, <c>occurred_at</c>) serves the reconciliation re-derivation —
/// the per-purchase "latest notification" lookup and the drifted-purchase candidate scan — so reconciliation never
/// table-scans the ledger. It is distinct from the existing UNIQUE
/// (<c>provider</c>, <c>provider_notification_id</c>) idempotency index, which is unchanged.
///
/// Rollback: <see cref="Down"/> drops the index and the column.
/// </summary>
public partial class AddStoreNotificationEventOccurredAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add nullable first so existing rows can be back-filled before the NOT NULL constraint is applied.
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "occurred_at",
            table: "store_notification_events",
            type: "timestamp with time zone",
            nullable: true);

        // Back-fill historical rows from their delivery time — the most faithful available proxy for the store's
        // event time — so every existing notification keeps a meaningful, ordered timestamp.
        migrationBuilder.Sql(
            "UPDATE store_notification_events SET occurred_at = received_at WHERE occurred_at IS NULL;");

        migrationBuilder.AlterColumn<DateTimeOffset>(
            name: "occurred_at",
            table: "store_notification_events",
            type: "timestamp with time zone",
            nullable: false,
            oldClrType: typeof(DateTimeOffset),
            oldType: "timestamp with time zone",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_store_notification_events_provider_transaction_occurred_at",
            table: "store_notification_events",
            columns: new[] { "provider", "provider_transaction_id", "occurred_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_store_notification_events_provider_transaction_occurred_at",
            table: "store_notification_events");

        migrationBuilder.DropColumn(
            name: "occurred_at",
            table: "store_notification_events");
    }
}
