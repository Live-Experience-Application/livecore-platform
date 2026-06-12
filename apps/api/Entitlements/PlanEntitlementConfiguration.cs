using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="PlanEntitlement"/> to the <c>plan_entitlements</c> table (CORE-ENTL-001) —
/// the granted-entitlement rows of a <see cref="PlanDefinition"/> (csv/database_tables.csv: module
/// Entitlements, scope <c>global</c>, "Plan-to-entitlement grant"). A grant is a CHILD of the plan aggregate:
/// the parent relationship, its <c>plan_definition_id</c> foreign key and the cascade-on-delete are configured
/// on the plan side in <see cref="PlanDefinitionConfiguration"/>; this configuration maps the grant's own
/// columns and its reference to the entitlement definition it grants.
///
/// VALUE COLUMNS. <c>value_kind</c> stores the enum NAME (Flag/Quota) via <c>HasConversion&lt;string&gt;</c>
/// (mirrors export_jobs.scope), and the granted value lives in two NULLABLE columns whose meaning is fixed by
/// the kind: <c>flag_value</c> (the boolean, set only for a flag grant) and <c>quota_limit</c> (the numeric
/// cap, set only for a quota grant; NULL for a quota means an unlimited/fair-use grant). The aggregate's
/// constructor enforces that exactly the right column is populated for the kind, so a row can never carry an
/// ambiguous value.
///
/// FOREIGN KEYS. <c>plan_definition_id</c> -&gt; <c>plan_definitions(id)</c> cascades (configured on the
/// parent side): a plan's grants are removed with it. <c>entitlement_definition_id</c> -&gt;
/// <c>entitlement_definitions(id)</c> is RESTRICTED on delete: a referenced entitlement definition cannot be
/// hard-deleted out from under a plan grant (the definition is soft-retired via <c>is_active</c> instead;
/// docs/10_DATABASE_SCHEMA.md), so a grant can never dangle.
///
/// A UNIQUE index on (<c>plan_definition_id</c>, <c>entitlement_definition_id</c>) is the per-plan natural key:
/// a plan grants each entitlement at most once, so a plan can never carry two conflicting grants for the same
/// entitlement (mirrors the unique export_manifest_entries(export_manifest_id, kind)).
/// </summary>
internal sealed class PlanEntitlementConfiguration : IEntityTypeConfiguration<PlanEntitlement>
{
    public void Configure(EntityTypeBuilder<PlanEntitlement> builder)
    {
        builder.ToTable("plan_entitlements");

        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(grant => grant.PlanDefinitionId)
            .HasColumnName("plan_definition_id")
            .IsRequired();

        builder.Property(grant => grant.EntitlementDefinitionId)
            .HasColumnName("entitlement_definition_id")
            .IsRequired();

        builder.Property(grant => grant.ValueKind)
            .HasColumnName("value_kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(grant => grant.FlagValue)
            .HasColumnName("flag_value")
            .IsRequired(false);

        builder.Property(grant => grant.QuotaLimit)
            .HasColumnName("quota_limit")
            .IsRequired(false);

        // Per-plan unique natural key: a plan grants each entitlement at most once.
        builder.HasIndex(grant => new { grant.PlanDefinitionId, grant.EntitlementDefinitionId })
            .IsUnique()
            .HasDatabaseName("ix_plan_entitlements_plan_definition_id_entitlement_definition_id");

        // Restrict deletion of a referenced entitlement definition: a definition that a plan grants cannot be
        // hard-deleted (it is soft-retired via is_active instead), so a grant can never dangle.
        builder.HasOne<EntitlementDefinition>()
            .WithMany()
            .HasForeignKey(grant => grant.EntitlementDefinitionId)
            .HasConstraintName("fk_plan_entitlements_entitlement_definitions_entitlement_definition_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
