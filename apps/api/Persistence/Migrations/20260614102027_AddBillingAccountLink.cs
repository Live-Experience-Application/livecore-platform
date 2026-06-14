using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Creates the Store module's <c>billing_account_links</c> table — the durable link from a verified purchase to
/// the authenticated BUYER (a subject) — CORE-MON-002, the buyer-linkage story of the "Monetization v1" epic
/// (docs/24_SPEC_CONSISTENCY.md "Decision recorded (CORE-MON-001)"; csv/database_tables.csv: module Store).
/// CORE-STORE-002 deliberately gave <c>purchase_transactions</c> NO buyer column (a purchase is named globally by
/// its provider + provider_transaction_id pair, so the authenticated buyer was verified then discarded); this is
/// the missing link that records WHICH subject a verified purchase belongs to, so CORE-MON-003 can grant that
/// subject the mapped entitlement.
///
/// THE STORY ACCEPTANCE CRITERION — "A verified purchase is durably linked to the authenticated buyer (subject)
/// so it can later grant that subject an entitlement; the same external receipt cannot be claimed by two
/// different subjects." A row binds one <c>purchase_transaction_id</c> to one buyer subject (<c>subject_type</c>,
/// <c>subject_id</c>) at the <c>linked_at</c> timestamp.
///
/// THE ONE-SUBJECT-PER-RECEIPT GUARANTEE. The single UNIQUE index
/// <c>ix_billing_account_links_purchase_transaction_id</c> on <c>purchase_transaction_id</c> is the guarantee — a
/// verified purchase (the receipt, collapsed to one <c>purchase_transactions</c> row by its idempotency key) can
/// be linked to only one subject, so a concurrent or repeated claim by a different subject is rejected at the
/// database and the same external receipt can never be claimed by two different subjects.
///
/// FOREIGN KEY, POLYMORPHIC SUBJECT. <c>purchase_transaction_id</c> -&gt; <c>purchase_transactions(id)</c> is
/// CASCADE on delete: the buyer link is part of the purchase's lifecycle (mirrors
/// <c>purchase_events.purchase_transaction_id</c>), so a purchase and its link are removed together.
/// <c>subject_id</c> is a generic POLYMORPHIC reference with NO database foreign key (mirrors
/// <c>subject_entitlements.subject_id</c>), so isolation is purely by the subject pair. Rollback:
/// <see cref="Down"/> drops the table.
/// </summary>
public partial class AddBillingAccountLink : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "billing_account_links",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                subject_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_billing_account_links", x => x.id);
                table.ForeignKey(
                    name: "fk_billing_account_links_purchase_transactions_purchase_transaction_id",
                    column: x => x.purchase_transaction_id,
                    principalTable: "purchase_transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_billing_account_links_purchase_transaction_id",
            table: "billing_account_links",
            column: "purchase_transaction_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "billing_account_links");
    }
}
