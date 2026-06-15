using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Store;

/// <summary>
/// Relational mapping of <see cref="BillingAccountLink"/> to the <c>billing_account_links</c> table
/// (CORE-MON-002; csv/database_tables.csv: module Store; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md /
/// docs/24_SPEC_CONSISTENCY.md "Database additions": <c>billing_account_links</c>). A row binds one verified
/// purchase (<c>purchase_transaction_id</c>) to one buyer subject (<c>subject_type</c>, <c>subject_id</c>) at the
/// <c>linked_at</c> timestamp.
///
/// THE ONE-SUBJECT-PER-RECEIPT GUARANTEE. The single documented critical index is the UNIQUE index on
/// <c>purchase_transaction_id</c>: a verified purchase (the receipt, collapsed to one <c>purchase_transactions</c>
/// row by its idempotency key) can be linked to only one subject — a concurrent or repeated insert for the same
/// purchase is rejected at the database and surfaces as
/// <see cref="BillingAccountLinkAddResult.DuplicatePurchase"/>, so the same external receipt can never be claimed
/// by two different subjects (the story acceptance criterion).
///
/// FOREIGN KEY. <c>purchase_transaction_id</c> -&gt; <c>purchase_transactions(id)</c> is CASCADE on delete: the
/// link is part of the purchase's lifecycle (mirrors <c>purchase_events.purchase_transaction_id</c>), so removing
/// a purchase removes its buyer link rather than leaving it dangling. <c>subject_id</c> is a generic POLYMORPHIC
/// reference with NO database foreign key (mirrors <c>subject_entitlements.subject_id</c>,
/// <c>visibility_rules.resource_id</c> and <c>asset_links.target_id</c>): a user subject and a workspace subject
/// that happen to share a guid never collide, and isolation is purely by the subject pair.
///
/// ENUM AS A STABLE NAME. <c>subject_type</c> is persisted by its stable enum NAME via
/// <c>HasConversion&lt;string&gt;</c> (mirrors <c>subject_entitlements.subject_type</c>), never as an integer and
/// never inside JSON — the subject kind is core authorization state, not a flexible attribute
/// (docs/10_DATABASE_SCHEMA.md).
/// </summary>
internal sealed class BillingAccountLinkConfiguration : IEntityTypeConfiguration<BillingAccountLink>
{
    public void Configure(EntityTypeBuilder<BillingAccountLink> builder)
    {
        builder.ToTable("billing_account_links");

        builder.HasKey(link => link.Id);

        builder.Property(link => link.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(link => link.PurchaseTransactionId)
            .HasColumnName("purchase_transaction_id")
            .IsRequired();

        builder.Property(link => link.SubjectType)
            .HasColumnName("subject_type")
            // Persist the subject type as its stable name (mirrors subject_entitlements.subject_type).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(link => link.SubjectId)
            .HasColumnName("subject_id")
            .IsRequired();

        builder.Property(link => link.LinkedAt)
            .HasColumnName("linked_at")
            .IsRequired();

        // Documented critical index billing_account_links(purchase_transaction_id), UNIQUE: the uniqueness IS the
        // one-subject-per-receipt guarantee — a verified purchase is linked to only one subject.
        builder.HasIndex(link => link.PurchaseTransactionId)
            .HasDatabaseName("ix_billing_account_links_purchase_transaction_id")
            .IsUnique();

        // Subject lookup index (CORE-MON-012): the cross-product entitlement-retention check reads ALL of one
        // subject's linked purchases (ListLinkedPurchasesBySubjectAsync) to decide whether a shared entitlement is
        // still granted by another active purchase. Non-unique — a subject may hold many purchases — and mirrors the
        // (subject_type, subject_id) lookup prefix on subject_entitlements.
        builder.HasIndex(link => new { link.SubjectType, link.SubjectId })
            .HasDatabaseName("ix_billing_account_links_subject_type_subject_id");

        // The purchase the link binds. CASCADE on delete: the buyer link is part of the purchase's lifecycle
        // (mirrors purchase_events), so a purchase and its link are removed together.
        builder.HasOne<PurchaseTransaction>()
            .WithMany()
            .HasForeignKey(link => link.PurchaseTransactionId)
            .HasConstraintName("fk_billing_account_links_purchase_transactions_purchase_transaction_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
