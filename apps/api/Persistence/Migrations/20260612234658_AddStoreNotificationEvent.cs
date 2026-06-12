using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Store module's append-only <c>store_notification_events</c> table — the idempotency ledger and
/// audit fact of every handled store server notification (CORE-STORE-005, the "Store Notifications" epic;
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions": <c>store_notification_events</c>;
/// csv/database_tables.csv: module Store). CORE-STORE-002 modeled the purchase transaction and its
/// <c>purchase_events</c> audit trail and deferred "the idempotent ingestion of those notifications" to here.
///
/// THE STORY ACCEPTANCE CRITERION — "Renewals, cancellations, refunds and grace periods update entitlements
/// safely." A row records one handled notification: the <c>provider</c>, the store-assigned
/// <c>provider_notification_id</c> (its idempotency key), the actionable <c>notification_type</c>, the affected
/// purchase's <c>provider_transaction_id</c>, the <c>applied_status</c> the notification drove the purchase to,
/// the processing <c>outcome</c> and the <c>received_at</c> timestamp. The purchase lifecycle change the
/// notification caused is separately audited by the <c>purchase_events</c> trail (CORE-STORE-002), so this row is
/// the notification-side auditable fact.
///
/// IDEMPOTENCY. The single UNIQUE index <c>ix_store_notification_events_provider_provider_notification_id</c> on
/// (<c>provider</c>, <c>provider_notification_id</c>) is the idempotency guarantee — a store notification id is
/// unique within its provider, so the same delivered notification is handled only once and a re-delivered or
/// replayed notification creates no second row ("Store notifications must be idempotent", docs/21), the same shape
/// as the unique <c>purchase_transactions(provider, provider_transaction_id)</c> and
/// <c>idempotency_keys(scope, key)</c> indexes.
///
/// NO FOREIGN KEY, NO RAW PAYLOAD. <c>provider_transaction_id</c> is a recorded identifier, not a database foreign
/// key: a notification can legitimately arrive for a purchase Core never recorded (handled fail-closed), and the
/// notification fact must survive regardless (mirrors how <c>audit_logs</c> records references as facts). Only the
/// normalized identifiers are stored — never the raw notification body, which may embed signed receipt content
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Rollback: <see cref="Down"/> drops the table.
/// </summary>
public partial class AddStoreNotificationEvent : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "store_notification_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                provider_notification_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                notification_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                provider_transaction_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                applied_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_store_notification_events", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_store_notification_events_provider_provider_notification_id",
            table: "store_notification_events",
            columns: new[] { "provider", "provider_notification_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "store_notification_events");
    }
}
