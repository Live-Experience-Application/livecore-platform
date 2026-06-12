using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="QuotaDefinition"/> to the <c>quota_definitions</c> table (CORE-ENTL-003, the
/// quota definition and quota status story of the "Entitlements and Quotas" epic; csv/database_tables.csv: module
/// Entitlements, scope <c>global</c>; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions":
/// <c>quota_definitions</c>). A row says HOW a numeric quota entitlement is measured: the measured
/// <c>entitlement_definition_id</c> with its denormalized stable <c>entitlement_key</c>, the <c>subject_type</c>
/// the usage is counted for, the <c>unit</c>, the <c>is_active</c> soft-lifecycle flag and the timestamps.
///
/// GLOBAL SCOPE, NOT TENANT-SCOPED. Like <c>entitlement_definitions</c>/<c>plan_definitions</c>, the quota
/// definition is the deployment-wide monetization catalog (csv/database_tables.csv scope <c>global</c>), so it
/// carries NO <c>organization_id</c> column and no tenant foreign key; there is no per-tenant copy to isolate
/// (threat T5 tenant isolation applies to tenant-scoped tables, not to a global definition catalog).
///
/// VALUE COLUMNS. <c>subject_type</c> and <c>unit</c> store the enum NAME via <c>HasConversion&lt;string&gt;</c>
/// (mirrors <c>subject_entitlements.subject_type</c> and <c>entitlement_definitions.value_kind</c>), so the stored
/// values stay readable and are not coupled to the declaration-order integers. <c>entitlement_key</c> is
/// denormalized from the (immutable) measured definition key so the row is self-describing and the quota-status
/// lookup is join-free.
///
/// INDEX AND FOREIGN KEY. The unique index on <c>entitlement_definition_id</c> is the catalog integrity guarantee:
/// one entitlement is measured by at most one quota definition. The (non-unique) <c>subject_type</c> index backs
/// the critical <see cref="IQuotaDefinitionRepository.ListActiveBySubjectTypeAsync"/> lookup. The
/// <c>entitlement_definition_id</c> -&gt; <c>entitlement_definitions(id)</c> foreign key is RESTRICTED on delete:
/// a measured definition cannot be hard-deleted out from under a quota definition (it is soft-retired via
/// <c>is_active</c> instead; docs/10_DATABASE_SCHEMA.md), so a quota definition can never dangle.
/// </summary>
internal sealed class QuotaDefinitionConfiguration : IEntityTypeConfiguration<QuotaDefinition>
{
    public void Configure(EntityTypeBuilder<QuotaDefinition> builder)
    {
        builder.ToTable("quota_definitions");

        builder.HasKey(definition => definition.Id);

        builder.Property(definition => definition.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(definition => definition.EntitlementDefinitionId)
            .HasColumnName("entitlement_definition_id")
            .IsRequired();

        builder.Property(definition => definition.EntitlementKey)
            .HasColumnName("entitlement_key")
            .HasMaxLength(EntitlementDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(definition => definition.SubjectType)
            .HasColumnName("subject_type")
            // Persist the subject type as its stable name (mirrors subject_entitlements.subject_type).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(definition => definition.Unit)
            .HasColumnName("unit")
            // Persist the unit as its stable name (mirrors the value-kind/subject-type conversions).
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(definition => definition.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(definition => definition.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(definition => definition.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Catalog integrity: one entitlement is measured by at most one quota definition.
        builder.HasIndex(definition => definition.EntitlementDefinitionId)
            .IsUnique()
            .HasDatabaseName("ix_quota_definitions_entitlement_definition_id");

        // Critical lookup index: the quota-status calculation lists active quotas by subject kind.
        builder.HasIndex(definition => definition.SubjectType)
            .HasDatabaseName("ix_quota_definitions_subject_type");

        // Restrict deletion of a measured entitlement definition: a definition a quota measures cannot be
        // hard-deleted (it is soft-retired via is_active instead), so a quota definition can never dangle.
        builder.HasOne<EntitlementDefinition>()
            .WithMany()
            .HasForeignKey(definition => definition.EntitlementDefinitionId)
            .HasConstraintName("fk_quota_definitions_entitlement_definitions_entitlement_definition_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
