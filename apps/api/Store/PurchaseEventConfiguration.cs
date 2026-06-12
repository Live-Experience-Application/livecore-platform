using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Store;

/// <summary>
/// Relational mapping of <see cref="PurchaseEvent"/> to the append-only <c>purchase_events</c> table
/// (CORE-STORE-002; csv/database_tables.csv: module Store; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md
/// "Database additions": <c>purchase_events</c>). Each row is one immutable purchase state change: the
/// <c>purchase_transaction_id</c> it belongs to, the <c>previous_status</c> (NULL for the initial recording) and
/// the resulting <c>new_status</c>, with the <c>created_at</c> timestamp. The trail is the "auditable" half of
/// the story ("All purchase state changes must be auditable", docs/21).
///
/// APPEND-ONLY (mirrors <c>audit_logs</c> and <c>session_events</c>, docs/10_DATABASE_SCHEMA.md). The repository
/// exposes only an append and a per-transaction read — no update, no delete — because a recorded state change is
/// an immutable fact.
///
/// FOREIGN KEY — THE OWNING TRANSACTION. <c>purchase_transaction_id</c> -&gt; <c>purchase_transactions(id)</c>
/// CASCADES on delete: the event trail is part of the transaction's own history, so the rare hard-delete of a
/// transaction removes its events with it. The documented critical index is
/// <c>purchase_events(purchase_transaction_id, created_at)</c> — events are read per transaction in time order,
/// so the composite index leads with the transaction column and then the timestamp. It is NON-unique (a
/// transaction has many events).
///
/// ENUMS AS STABLE NAMES, NO JSON. <c>previous_status</c> and <c>new_status</c> are persisted by their stable
/// enum NAME via <c>HasConversion&lt;string&gt;</c> (mirrors <c>purchase_transactions.status</c>); they are
/// first-class string columns, never JSON. No proof and no receipt content is ever stored (threat T7).
/// </summary>
internal sealed class PurchaseEventConfiguration : IEntityTypeConfiguration<PurchaseEvent>
{
    public void Configure(EntityTypeBuilder<PurchaseEvent> builder)
    {
        builder.ToTable("purchase_events");

        builder.HasKey(purchaseEvent => purchaseEvent.Id);

        builder.Property(purchaseEvent => purchaseEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(purchaseEvent => purchaseEvent.PurchaseTransactionId)
            .HasColumnName("purchase_transaction_id")
            .IsRequired();

        // Nullable: the initial recording has no prior state. Persisted by the enum's stable name.
        builder.Property(purchaseEvent => purchaseEvent.PreviousStatus)
            .HasColumnName("previous_status")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(purchaseEvent => purchaseEvent.NewStatus)
            .HasColumnName("new_status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(purchaseEvent => purchaseEvent.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Documented critical index purchase_events(purchase_transaction_id, created_at): reads lead with the
        // transaction column and then the timestamp. NON-unique.
        builder.HasIndex(purchaseEvent => new { purchaseEvent.PurchaseTransactionId, purchaseEvent.CreatedAt })
            .HasDatabaseName("ix_purchase_events_purchase_transaction_id_created_at");

        // The owning transaction: every event hangs off exactly one purchase transaction, and CASCADES with it.
        builder.HasOne<PurchaseTransaction>()
            .WithMany()
            .HasForeignKey(purchaseEvent => purchaseEvent.PurchaseTransactionId)
            .HasConstraintName("fk_purchase_events_purchase_transactions_purchase_transaction_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
