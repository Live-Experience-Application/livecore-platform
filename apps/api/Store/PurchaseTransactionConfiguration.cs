using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Store;

/// <summary>
/// Relational mapping of <see cref="PurchaseTransaction"/> to the <c>purchase_transactions</c> table
/// (CORE-STORE-002; csv/database_tables.csv: module Store; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md
/// "Database additions": <c>purchase_transactions</c>). A row is one verified store purchase: the
/// <c>provider</c> and the provider-assigned <c>provider_transaction_id</c> (its idempotency key), the
/// <c>product_reference</c>, the current <c>status</c> and the record/update timestamps.
///
/// NOT TENANT-SCOPED. The table carries NO <c>organization_id</c> and NO buyer column: a purchase is named
/// globally by its (<c>provider</c>, <c>provider_transaction_id</c>) pair, and the buyer/subject linkage is the
/// separate <c>billing_account_links</c> table (CORE-MON-002, <see cref="BillingAccountLinkConfiguration"/>),
/// exactly as docs/21 lists the two as distinct "Database additions".
///
/// THE IDEMPOTENCY GUARANTEE. The single documented critical index is the UNIQUE composite index on
/// (<c>provider</c>, <c>provider_transaction_id</c>): a provider transaction id is unique within its provider,
/// so the same verified purchase can be recorded only once — a concurrent or repeated insert is rejected at the
/// database and surfaces as <see cref="PurchaseTransactionAddResult.DuplicateTransaction"/> (the persistence
/// analogue of the unique <c>idempotency_keys(scope, key)</c> index). "Store notifications must be idempotent"
/// (docs/21).
///
/// ENUMS AS STABLE NAMES. <c>provider</c> and <c>status</c> are persisted by their stable enum NAME via
/// <c>HasConversion&lt;string&gt;</c> (mirrors <c>visibility_rules.visibility</c>, <c>sessions.status</c>), never
/// as an integer and never inside JSON — the status is core lifecycle state, not a flexible attribute
/// (docs/10_DATABASE_SCHEMA.md). The identifier columns are bounded strings.
/// </summary>
internal sealed class PurchaseTransactionConfiguration : IEntityTypeConfiguration<PurchaseTransaction>
{
    public void Configure(EntityTypeBuilder<PurchaseTransaction> builder)
    {
        builder.ToTable("purchase_transactions");

        builder.HasKey(transaction => transaction.Id);

        builder.Property(transaction => transaction.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(transaction => transaction.Provider)
            .HasColumnName("provider")
            // Persist the provider as its stable name (mirrors the other enums).
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(transaction => transaction.ProviderTransactionId)
            .HasColumnName("provider_transaction_id")
            .HasMaxLength(PurchaseTransaction.MaxIdentifierLength)
            .IsRequired();

        builder.Property(transaction => transaction.ProductReference)
            .HasColumnName("product_reference")
            .HasMaxLength(PurchaseTransaction.MaxIdentifierLength)
            .IsRequired();

        builder.Property(transaction => transaction.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(transaction => transaction.RecordedAt)
            .HasColumnName("recorded_at")
            .IsRequired();

        builder.Property(transaction => transaction.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Documented critical index purchase_transactions(provider, provider_transaction_id), UNIQUE: the
        // uniqueness IS the idempotency guarantee — the same verified purchase is recorded only once.
        builder.HasIndex(transaction => new { transaction.Provider, transaction.ProviderTransactionId })
            .HasDatabaseName("ix_purchase_transactions_provider_provider_transaction_id")
            .IsUnique();
    }
}
