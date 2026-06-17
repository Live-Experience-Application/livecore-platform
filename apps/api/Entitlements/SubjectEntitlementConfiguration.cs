// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Relational mapping of <see cref="SubjectEntitlement"/> to the <c>subject_entitlements</c> table
/// (CORE-ENTL-002, the subject entitlement assignment and lookup story of the "Entitlements and Quotas" epic;
/// csv/database_tables.csv: module Entitlements; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Database
/// additions": <c>subject_entitlements</c>). A row records that one generic subject — a user or a workspace —
/// holds a granted entitlement at a concrete value: the subject (<c>subject_type</c>, <c>subject_id</c>), the
/// granted <c>entitlement_definition_id</c> and its denormalized stable <c>entitlement_key</c>, the
/// <c>value_kind</c> with the granted <c>flag_value</c>/<c>quota_limit</c>, the optional
/// <c>source_plan_definition_id</c> provenance, the <c>is_active</c> lifecycle flag and the timestamps.
///
/// PER-SUBJECT, KEYED BY THE SUBJECT PAIR. The assignment carries NO <c>organization_id</c>: a subject's
/// premium state is keyed by the (<c>subject_type</c>, <c>subject_id</c>) pair, not by a tenant. The subject id
/// is a generic POLYMORPHIC reference with NO database foreign key (mirrors <c>asset_links.target_id</c>,
/// <c>visibility_rules.resource_id</c> and <c>session_events.visibility_subject_id</c>), so a user subject and
/// a workspace subject that happen to share a guid never collide and isolation is purely by the subject pair.
///
/// VALUE COLUMNS. <c>value_kind</c> stores the enum NAME (Flag/Quota) via <c>HasConversion&lt;string&gt;</c>
/// (mirrors <c>plan_entitlements.value_kind</c>), and the granted value lives in two NULLABLE columns whose
/// meaning is fixed by the kind: <c>flag_value</c> (set only for a flag) and <c>quota_limit</c> (set only for a
/// quota; NULL means an unlimited/fair-use grant). The aggregate enforces that exactly the right column is
/// populated for the kind, so a row can never carry an ambiguous value. <c>entitlement_key</c> is denormalized
/// from the (immutable) definition key so the row is self-describing and the premium-state lookup is join-free.
///
/// FOREIGN KEYS. <c>entitlement_definition_id</c> -&gt; <c>entitlement_definitions(id)</c> is RESTRICTED on
/// delete: a referenced entitlement definition cannot be hard-deleted out from under an assignment (it is
/// soft-retired via <c>is_active</c> instead; docs/10_DATABASE_SCHEMA.md), so an assignment can never dangle.
/// <c>source_plan_definition_id</c> -&gt; <c>plan_definitions(id)</c> is nullable and SET NULL on delete (the
/// provenance is retained as a fact and survives the rare hard-delete of a plan; mirrors
/// <c>export_jobs.requested_by</c>).
///
/// A UNIQUE index on (<c>subject_type</c>, <c>subject_id</c>, <c>entitlement_definition_id</c>) is the
/// per-subject natural key: a subject holds each entitlement at most once, so it can never carry two
/// conflicting assignments for the same entitlement (mirrors the unique <c>plan_entitlements(plan_definition_id,
/// entitlement_definition_id)</c>). Its (<c>subject_type</c>, <c>subject_id</c>) prefix is also the critical
/// lookup index that resolves a subject's effective entitlements.
///
/// Deployment requirement: equality on <c>entitlement_key</c> must stay exact, exactly as noted for the
/// <c>entitlement_definitions(key)</c> column; the key is canonical (lower-case) in the domain model.
/// </summary>
internal sealed class SubjectEntitlementConfiguration : IEntityTypeConfiguration<SubjectEntitlement>
{
    public void Configure(EntityTypeBuilder<SubjectEntitlement> builder)
    {
        builder.ToTable("subject_entitlements");

        builder.HasKey(assignment => assignment.Id);

        builder.Property(assignment => assignment.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(assignment => assignment.SubjectType)
            .HasColumnName("subject_type")
            // Persist the subject type as its stable name (mirrors the value-kind conversion).
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(assignment => assignment.SubjectId)
            .HasColumnName("subject_id")
            .IsRequired();

        builder.Property(assignment => assignment.EntitlementDefinitionId)
            .HasColumnName("entitlement_definition_id")
            .IsRequired();

        builder.Property(assignment => assignment.EntitlementKey)
            .HasColumnName("entitlement_key")
            .HasMaxLength(EntitlementDefinition.MaxKeyLength)
            .IsRequired();

        builder.Property(assignment => assignment.ValueKind)
            .HasColumnName("value_kind")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(assignment => assignment.FlagValue)
            .HasColumnName("flag_value")
            .IsRequired(false);

        builder.Property(assignment => assignment.QuotaLimit)
            .HasColumnName("quota_limit")
            .IsRequired(false);

        builder.Property(assignment => assignment.SourcePlanDefinitionId)
            .HasColumnName("source_plan_definition_id")
            .IsRequired(false);

        builder.Property(assignment => assignment.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(assignment => assignment.GrantedAt)
            .HasColumnName("granted_at")
            .IsRequired();

        builder.Property(assignment => assignment.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Per-subject unique natural key: a subject holds each entitlement at most once. Its
        // (subject_type, subject_id) prefix is the critical lookup index for resolving a subject's effective
        // entitlements.
        builder.HasIndex(assignment => new { assignment.SubjectType, assignment.SubjectId, assignment.EntitlementDefinitionId })
            .IsUnique()
            .HasDatabaseName("ix_subject_entitlements_subject_type_subject_id_entitlement_definition_id");

        // Restrict deletion of a referenced entitlement definition: a definition a subject is assigned cannot
        // be hard-deleted (it is soft-retired via is_active instead), so an assignment can never dangle.
        builder.HasOne<EntitlementDefinition>()
            .WithMany()
            .HasForeignKey(assignment => assignment.EntitlementDefinitionId)
            .HasConstraintName("fk_subject_entitlements_entitlement_definitions_entitlement_definition_id")
            .OnDelete(DeleteBehavior.Restrict);

        // Source plan provenance: nullable and SET NULL on delete so the provenance survives the rare
        // hard-delete of a plan (a plan is normally soft-retired via is_active).
        builder.HasOne<PlanDefinition>()
            .WithMany()
            .HasForeignKey(assignment => assignment.SourcePlanDefinitionId)
            .HasConstraintName("fk_subject_entitlements_plan_definitions_source_plan_definition_id")
            .OnDelete(DeleteBehavior.SetNull);
    }
}
