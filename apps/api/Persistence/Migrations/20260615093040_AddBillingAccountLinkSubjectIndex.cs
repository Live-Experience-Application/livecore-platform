using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the non-unique subject lookup index <c>ix_billing_account_links_subject_type_subject_id</c> on
/// <c>billing_account_links(subject_type, subject_id)</c> — CORE-MON-012, the "Narrow cross-product entitlement
/// over-revocation" story of the "Monetization v1" epic (csv/database_tables.csv: module Store; docs/10_DATABASE_SCHEMA.md).
///
/// The cross-product entitlement-retention check reads ALL of one subject's linked purchases
/// (<c>ListLinkedPurchasesBySubjectAsync</c>) to decide whether a refunded purchase's entitlement is still granted by
/// another of the subject's active purchases and so must be retained. That reverse, subject-scoped read is served by
/// this index, mirroring the (subject_type, subject_id) lookup prefix on <c>subject_entitlements</c>. It is NOT unique
/// — a subject may hold many purchases — and is independent of the existing UNIQUE
/// <c>ix_billing_account_links_purchase_transaction_id</c> (the one-subject-per-receipt guarantee). The table and its
/// columns are unchanged; this adds only the index. Rollback: <see cref="Down"/> drops the index.
/// </summary>
public partial class AddBillingAccountLinkSubjectIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_billing_account_links_subject_type_subject_id",
            table: "billing_account_links",
            columns: new[] { "subject_type", "subject_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_billing_account_links_subject_type_subject_id",
            table: "billing_account_links");
    }
}
