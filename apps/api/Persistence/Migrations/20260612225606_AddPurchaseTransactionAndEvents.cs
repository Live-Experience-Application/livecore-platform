using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Store module's <c>purchase_transactions</c> and append-only <c>purchase_events</c> tables — the
/// verified purchase persistence and its audit trail (CORE-STORE-002, the second story of the "Store Purchase
/// Verification" epic; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions":
/// <c>purchase_transactions</c>, <c>purchase_events</c>; csv/database_tables.csv: module Store). CORE-STORE-001
/// modeled the provider abstraction that verifies a purchase proof and produces a normalized
/// <c>VerifiedPurchase</c>, and deferred "persistence of the verified transaction" to here.
///
/// THE STORY ACCEPTANCE CRITERION — "Purchase state changes are persisted and auditable."
/// <c>purchase_transactions</c> holds one verified purchase: the <c>provider</c>, the provider-assigned
/// <c>provider_transaction_id</c>, the <c>product_reference</c>, the current lifecycle <c>status</c> and the
/// record/update timestamps. <c>purchase_events</c> is the immutable per-transaction audit trail: each row
/// records one state change as a <c>previous_status</c> (NULL for the initial recording) -&gt; <c>new_status</c>
/// pair with a <c>created_at</c> timestamp, so a purchase's lifecycle is fully reconstructable (docs/21 "All
/// purchase state changes must be auditable").
///
/// IDEMPOTENCY. The single UNIQUE index <c>ix_purchase_transactions_provider_provider_transaction_id</c> on
/// (<c>provider</c>, <c>provider_transaction_id</c>) is the idempotency guarantee — a provider transaction id is
/// unique within its provider, so the same verified purchase is recorded only once and a client retry, a
/// replayed proof or a duplicate notification creates no second row ("Store notifications must be idempotent",
/// docs/21), the persistence analogue of the unique <c>idempotency_keys(scope, key)</c> index.
///
/// NOT TENANT-SCOPED. <c>purchase_transactions</c> carries NO <c>organization_id</c> and NO buyer column: a
/// purchase is named globally by its (<c>provider</c>, <c>provider_transaction_id</c>) pair, and the
/// buyer/subject linkage is the separate <c>billing_account_links</c> table (a later story), exactly as docs/21
/// lists the two as distinct additions.
///
/// FOREIGN KEY AND INDEX. <c>purchase_events.purchase_transaction_id</c> -&gt; <c>purchase_transactions(id)</c>
/// CASCADES on delete — the trail is part of the transaction's own history. The non-unique
/// <c>ix_purchase_events_purchase_transaction_id_created_at</c> is the documented critical index: events are read
/// per transaction in time order. Rollback: <see cref="Down"/> drops both tables (the events table first, since
/// it references the transactions table).
/// </summary>
public partial class AddPurchaseTransactionAndEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "purchase_transactions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                provider_transaction_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                product_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_purchase_transactions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "purchase_events",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                previous_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                new_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_purchase_events", x => x.id);
                table.ForeignKey(
                    name: "fk_purchase_events_purchase_transactions_purchase_transaction_id",
                    column: x => x.purchase_transaction_id,
                    principalTable: "purchase_transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_purchase_events_purchase_transaction_id_created_at",
            table: "purchase_events",
            columns: new[] { "purchase_transaction_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_purchase_transactions_provider_provider_transaction_id",
            table: "purchase_transactions",
            columns: new[] { "provider", "provider_transaction_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "purchase_events");

        migrationBuilder.DropTable(
            name: "purchase_transactions");
    }
}
