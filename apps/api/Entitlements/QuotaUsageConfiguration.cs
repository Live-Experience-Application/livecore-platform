using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="QuotaUsage"/> to the <c>quota_usage</c> table (CORE-ENTL-003, the quota
/// definition and quota status story of the "Entitlements and Quotas" epic; csv/database_tables.csv: module
/// Entitlements; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions": <c>quota_usage</c>). A row
/// records how much of a quota one generic subject — a user or a workspace — currently consumes: the subject
/// (<c>subject_type</c>, <c>subject_id</c>), the measured <c>quota_definition_id</c> and its denormalized stable
/// <c>entitlement_key</c>, the <c>used_amount</c> and the timestamps.
///
/// PER-SUBJECT, KEYED BY THE SUBJECT PAIR. The row carries NO <c>organization_id</c>: usage is keyed by the
/// (<c>subject_type</c>, <c>subject_id</c>) pair, not by a tenant. The subject id is a generic POLYMORPHIC
/// reference with NO database foreign key (mirrors <c>subject_entitlements.subject_id</c>,
/// <c>asset_links.target_id</c> and <c>visibility_rules.resource_id</c>), so a user subject and a workspace
/// subject that share a guid never collide and isolation is purely by the subject pair.
///
/// INDEXES AND FOREIGN KEY. The unique
/// <c>ix_quota_usage_subject_type_subject_id_quota_definition_id</c> is the per-subject natural key (a subject
/// records each quota at most once); its (<c>subject_type</c>, <c>subject_id</c>) prefix is the critical lookup
/// index that reads a subject's usage. <c>quota_definition_id</c> -&gt; <c>quota_definitions(id)</c> is RESTRICTED
/// on delete: a referenced quota definition cannot be hard-deleted out from under a usage row (it is soft-retired
/// via <c>is_active</c> instead; docs/10_DATABASE_SCHEMA.md), so usage can never dangle.
///
/// Deployment requirement: equality on <c>entitlement_key</c> stays exact, exactly as noted for the
/// <c>entitlement_definitions(key)</c> column; the key is canonical (lower-case) in the domain model.
/// </summary>
internal sealed class QuotaUsageConfiguration : IEntityTypeConfiguration<QuotaUsage>
{
    public void Configure(EntityTypeBuilder<QuotaUsage> builder)
    {
        builder.ToTable("quota_usage");

        builder.HasKey(usage => usage.Id);

        builder.Property(usage => usage.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(usage => usage.SubjectType)
            .HasColumnName("subject_type")
            // Persist the subject type as its stable name (mirrors subject_entitlements.subject_type).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(usage => usage.SubjectId)
            .HasColumnName("subject_id")
            .IsRequired();

        builder.Property(usage => usage.QuotaDefinitionId)
            .HasColumnName("quota_definition_id")
            .IsRequired();

        builder.Property(usage => usage.EntitlementKey)
            .HasColumnName("entitlement_key")
            .HasMaxLength(EntitlementDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(usage => usage.UsedAmount)
            .HasColumnName("used_amount")
            .IsRequired();

        builder.Property(usage => usage.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(usage => usage.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Per-subject unique natural key: a subject records each quota at most once. Its
        // (subject_type, subject_id) prefix is the critical lookup index for reading a subject's usage.
        builder.HasIndex(usage => new { usage.SubjectType, usage.SubjectId, usage.QuotaDefinitionId })
            .IsUnique()
            .HasDatabaseName("ix_quota_usage_subject_type_subject_id_quota_definition_id");

        // Restrict deletion of a referenced quota definition: a quota a subject records usage against cannot be
        // hard-deleted (it is soft-retired via is_active instead), so usage can never dangle.
        builder.HasOne<QuotaDefinition>()
            .WithMany()
            .HasForeignKey(usage => usage.QuotaDefinitionId)
            .HasConstraintName("fk_quota_usage_quota_definitions_quota_definition_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
