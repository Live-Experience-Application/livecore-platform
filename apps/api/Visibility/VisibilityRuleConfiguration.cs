using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Relational mapping of <see cref="VisibilityRule"/> to the <c>visibility_rules</c> table
/// (CORE-VIS-001; csv/database_tables.csv: module Visibility, scope <c>workspace</c>, "Audience
/// rules"). The rule is workspace-scoped, so it carries a <c>workspace_id</c> column that is a
/// foreign key into <c>workspaces(id)</c>, and tenant-scoped, so it carries an <c>organization_id</c>
/// column that is a foreign key into <c>organizations(id)</c> (docs/10_DATABASE_SCHEMA.md principle:
/// "tenant-scoped tables include <c>organization_id</c>", "workspace-scoped tables include
/// <c>workspace_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The owning workspace already
/// pins the organization, but storing the tenant on the row lets isolation be enforced at the row
/// level and the organization boundary be checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
///
/// Rule columns are REAL COLUMNS, never JSON: <c>resource_type</c> stores the generic governed
/// resource kind name (Scene/ContentBlock/Entity), <c>resource_id</c> the governed resource's
/// surrogate id, and <c>visibility</c> the base audience state name (Hidden/Visible). This honors
/// docs/10_DATABASE_SCHEMA.md: "Do not store visibility rules only inside arbitrary JSON" and JSONB
/// is "not for core authorization fields" — the authorization-relevant fields are first-class
/// columns the database can index and constrain. The two enums are persisted by their stable NAME
/// (not their numeric value), exactly like <c>content_type</c> on <c>content_blocks</c> and the
/// session/participant status columns.
///
/// <c>resource_id</c> is intentionally NOT a foreign key: the reference is polymorphic across the
/// <c>scenes</c>, <c>content_blocks</c> and <c>entities</c> tables, and a single column cannot
/// foreign-key into three principals. The rule is the polymorphic owner; the same-workspace coupling
/// between a rule and its resource is enforced by the application flow that creates rules (the later
/// reveal / visibility-rule command), mirroring the <c>ContentBlock.SceneId</c> and
/// <c>Entity.EntityTypeId</c> precedent.
///
/// Two indexes back the documented access patterns:
/// <list type="bullet">
///   <item>
///   The non-unique composite index on (<c>workspace_id</c>, <c>resource_type</c>,
///   <c>resource_id</c>) is the documented critical index
///   <c>visibility_rules(workspace_id, resource_type, resource_id)</c>
///   (docs/10_DATABASE_SCHEMA.md): it keeps "find the rule(s) for this resource" efficient and makes
///   every visibility read lead with the workspace column. It is NON-unique on purpose — a resource
///   may accumulate more than one rule (the base audience rule now; per-participant scoped rules in
///   the later selected-participant reveal, CORE-VIS-005) — so this story does not impose a
///   uniqueness a later story would have to drop.
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization foreign-key
///   check efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5).
///   </item>
/// </list>
///
/// Both foreign keys cascade on delete: a visibility rule has no meaning without its workspace or
/// its tenant, so removing the workspace or the organization removes its rules (mirrors the required
/// cascades on <c>content_blocks</c>, <c>scenes</c> and <c>entities</c>).
///
/// Deployment requirement: equality on the id/enum columns is binary, so no collation concern
/// applies. Pinning any column collation, if ever needed, is planned with the first story that runs
/// migrations against real PostgreSQL, exactly as noted for the other workspace-scoped tables.
/// </summary>
internal sealed class VisibilityRuleConfiguration : IEntityTypeConfiguration<VisibilityRule>
{
    public void Configure(EntityTypeBuilder<VisibilityRule> builder)
    {
        builder.ToTable("visibility_rules");

        builder.HasKey(rule => rule.Id);

        builder.Property(rule => rule.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(rule => rule.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(rule => rule.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(rule => rule.ResourceType)
            .HasColumnName("resource_type")
            // Persist the resource kind as its stable name (not its numeric value), mirrors
            // content_type on content_blocks.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(rule => rule.ResourceId)
            .HasColumnName("resource_id")
            .IsRequired();

        builder.Property(rule => rule.Visibility)
            .HasColumnName("visibility")
            // Persist the audience state as its stable name (Hidden/Visible), not its numeric value.
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(rule => rule.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(rule => rule.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // Selected-participant target (CORE-VIS-005): NULL = audience-wide, a set value scopes the
        // rule to one participant. A real column (not JSON), nullable.
        builder.Property(rule => rule.TargetParticipantId)
            .HasColumnName("target_participant_id");

        // Documented critical index visibility_rules(workspace_id, resource_type, resource_id):
        // resource reads lead with the workspace column (docs/10_DATABASE_SCHEMA.md). NON-unique on
        // purpose (a resource may accumulate multiple rules; see the type summary).
        builder.HasIndex(rule => new { rule.WorkspaceId, rule.ResourceType, rule.ResourceId })
            .HasDatabaseName("ix_visibility_rules_workspace_id_resource_type_resource_id");

        // Tenant-scoped composite index leading with organization_id: keeps the organization
        // boundary check (checked before the workspace boundary) and tenant-scoped reads efficient
        // (docs/10_DATABASE_SCHEMA.md; threat T5).
        builder.HasIndex(rule => new { rule.OrganizationId, rule.WorkspaceId })
            .HasDatabaseName("ix_visibility_rules_organization_id_workspace_id");

        // Tenant foreign key: every visibility rule hangs off exactly one organization (the owner of
        // its workspace). Cascade delete removes rules with their tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(rule => rule.OrganizationId)
            .HasConstraintName("fk_visibility_rules_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: every visibility rule hangs off exactly one workspace. Cascade
        // delete removes rules with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(rule => rule.WorkspaceId)
            .HasConstraintName("fk_visibility_rules_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Selected-participant foreign key (CORE-VIS-005): a participant-scoped rule references one
        // participant. It CASCADES on delete — a participant-scoped rule has no meaning without its
        // target participant, and cascade (not set-null) is the SAFE behavior: set-null would silently
        // turn a participant-scoped rule into an audience-wide one, over-sharing the resource (threat
        // T5). An audience-wide rule has a NULL target and is unaffected. EF maps the auto-index for
        // the foreign key. The same-workspace coupling (the target participant is in the rule's own
        // workspace) is enforced by the reveal application flow, mirroring resource_id.
        builder.HasOne<Participant>()
            .WithMany()
            .HasForeignKey(rule => rule.TargetParticipantId)
            .HasConstraintName("fk_visibility_rules_participants_target_participant_id")
            .OnDelete(DeleteBehavior.Cascade);

        // NOTE: resource_id is deliberately NOT mapped as a foreign key — the reference is
        // polymorphic across scenes/content_blocks/entities; the same-workspace coupling is enforced
        // by the application flow that creates rules (see the type summary).
    }
}
