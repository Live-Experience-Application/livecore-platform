using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="EntitlementDefinition"/> to the <c>entitlement_definitions</c> table
/// (CORE-ENTL-001; the Entitlements module's first table; csv/database_tables.csv: module Entitlements, scope
/// <c>global</c>, "Generic entitlement catalog entry"; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md
/// "Database additions": <c>entitlement_definitions</c>). A row is one generic entitlement: its stable
/// <c>key</c>, the <c>value_kind</c> (flag/quota), the generic <c>display_name</c> and optional
/// <c>description</c>, the <c>is_active</c> soft-lifecycle flag and the <c>created_at</c>/<c>updated_at</c>
/// timestamps.
///
/// GLOBAL SCOPE, NOT TENANT-SCOPED. The entitlement definition is the deployment-wide monetization catalog
/// (csv/database_tables.csv scope <c>global</c>), so — like <c>organizations</c>, <c>users</c> and a global
/// <c>templates</c> row — it carries NO <c>organization_id</c> column and no tenant foreign key; there is no
/// per-tenant copy to isolate (threat T5 tenant isolation applies to tenant-scoped tables, not to a global
/// definition catalog).
///
/// The unique index on <c>key</c> is the catalog integrity guarantee: one key maps to at most one entitlement
/// definition, so the stable natural key used for lookups and for plan/subject references can never address
/// two definitions. The <c>value_kind</c> column stores the enum NAME (Flag/Quota), persisted via
/// <c>HasConversion&lt;string&gt;</c> exactly like <c>export_jobs.scope</c>, so the stored value stays readable
/// and is not coupled to the declaration-order integers.
///
/// A definition is never hard-deleted (it is referenced by plan grants and later subject entitlements); it is
/// soft-retired via <c>is_active</c> (docs/10_DATABASE_SCHEMA.md: "avoid hard-delete for business data; use
/// soft delete where needed"), so this table has no foreign keys onto it that would cascade it away.
///
/// Deployment requirement: equality on <c>key</c> must stay exact. Keys are canonicalized to lower case in the
/// domain model (<see cref="EntitlementDefinition.CanonicalizeKey"/>), and the target database needs a
/// deterministic default collation (PostgreSQL default "C"/locale collations qualify) so the unique index and
/// lookups stay exact. Pinning the column collation in the model, if ever needed, is planned with the first
/// story that runs migrations against real PostgreSQL, exactly as noted for the <c>organizations</c> and
/// <c>users</c> tables.
/// </summary>
internal sealed class EntitlementDefinitionConfiguration : IEntityTypeConfiguration<EntitlementDefinition>
{
    public void Configure(EntityTypeBuilder<EntitlementDefinition> builder)
    {
        builder.ToTable("entitlement_definitions");

        builder.HasKey(definition => definition.Id);

        builder.Property(definition => definition.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(definition => definition.Key)
            .HasColumnName("key")
            .HasMaxLength(EntitlementDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(definition => definition.ValueKind)
            .HasColumnName("value_kind")
            // Persist the value kind as its stable name (mirrors export_jobs.scope).
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(definition => definition.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(EntitlementDefinition.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(definition => definition.Description)
            .HasColumnName("description")
            .HasMaxLength(EntitlementDefinition.MaxDescriptionLength)
            .IsRequired(false);

        builder.Property(definition => definition.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(definition => definition.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(definition => definition.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Catalog integrity: one key maps to at most one entitlement definition.
        builder.HasIndex(definition => definition.Key)
            .IsUnique()
            .HasDatabaseName("ix_entitlement_definitions_key");
    }
}
