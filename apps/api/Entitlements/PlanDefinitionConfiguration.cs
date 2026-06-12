using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="PlanDefinition"/> to the <c>plan_definitions</c> table (CORE-ENTL-001; the
/// Entitlements module's second table; csv/database_tables.csv: module Entitlements, scope <c>global</c>,
/// "Generic plan catalog entry"; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database additions":
/// <c>plan_definitions</c>). A row is one generic plan: its stable <c>key</c>, the generic <c>display_name</c>
/// and optional <c>description</c>, the <c>is_active</c> soft-lifecycle flag and the timestamps; its granted
/// entitlements live in the <c>plan_entitlements</c> child table (<see cref="PlanEntitlementConfiguration"/>).
///
/// GLOBAL SCOPE, NOT TENANT-SCOPED. Like <see cref="EntitlementDefinitionConfiguration"/>, the plan definition
/// is the deployment-wide monetization catalog (scope <c>global</c>), so it carries NO <c>organization_id</c>
/// column and no tenant foreign key.
///
/// The unique index on <c>key</c> is the catalog integrity guarantee: one key maps to at most one plan. A plan
/// is never hard-deleted (it is referenced by later subject assignments); it is soft-retired via
/// <c>is_active</c> (docs/10_DATABASE_SCHEMA.md).
///
/// The granted entitlements are mapped as a one-to-many relationship to <see cref="PlanEntitlement"/> over the
/// aggregate's read-only <see cref="PlanDefinition.Entitlements"/> collection (its backing field), with the
/// child's <c>plan_definition_id</c> foreign key CASCADING on delete so a plan's grants are removed with it
/// (mirrors export_manifests -> export_manifest_entries).
///
/// Deployment requirement: equality on <c>key</c> must stay exact, exactly as noted for the
/// <c>entitlement_definitions</c> and <c>organizations</c> tables.
/// </summary>
internal sealed class PlanDefinitionConfiguration : IEntityTypeConfiguration<PlanDefinition>
{
    public void Configure(EntityTypeBuilder<PlanDefinition> builder)
    {
        builder.ToTable("plan_definitions");

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(plan => plan.Key)
            .HasColumnName("key")
            .HasMaxLength(PlanDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(plan => plan.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(PlanDefinition.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(plan => plan.Description)
            .HasColumnName("description")
            .HasMaxLength(PlanDefinition.MaxDescriptionLength)
            .IsRequired(false);

        builder.Property(plan => plan.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(plan => plan.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(plan => plan.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Catalog integrity: one key maps to at most one plan.
        builder.HasIndex(plan => plan.Key)
            .IsUnique()
            .HasDatabaseName("ix_plan_definitions_key");

        // Granted entitlements: a one-to-many relationship to the child grants over the aggregate's read-only
        // Entitlements collection. The child's plan_definition_id foreign key cascades on delete so a plan's
        // grants are removed with it.
        builder.HasMany(plan => plan.Entitlements)
            .WithOne()
            .HasForeignKey(grant => grant.PlanDefinitionId)
            .HasConstraintName("fk_plan_entitlements_plan_definitions_plan_definition_id")
            .OnDelete(DeleteBehavior.Cascade);

        // The Entitlements collection is exposed read-only (IReadOnlyList over a backing field), so EF reads
        // and writes it through the field rather than the property setter.
        builder.Metadata
            .FindNavigation(nameof(PlanDefinition.Entitlements))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}
