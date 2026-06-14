using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Relational mapping of <see cref="VisibilityRule"/> to the <c>visibility_rules</c> table
/// (CORE-VIS-001, scoped to the session by CORE-SVIS-001; csv/database_tables.csv: module Visibility,
/// scope <c>session</c>, "Session-scoped audience rules"). A reveal is session-scoped
/// (docs/adr/0013-session-scoped-visibility-rules.md), so the rule carries a <c>session_id</c> column
/// that is a foreign key into <c>sessions(id)</c>, in addition to the <c>workspace_id</c> foreign key
/// into <c>workspaces(id)</c> and the <c>organization_id</c> foreign key into <c>organizations(id)</c>
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include <c>organization_id</c>",
/// "workspace-scoped tables include <c>workspace_id</c>", "session-scoped tables include
/// <c>session_id</c>"; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The owning session already pins
/// the workspace and tenant, but storing all three on the row lets isolation be enforced at the row
/// level and the boundaries be checked in order (organization, then workspace, then session) — and,
/// crucially, lets a reveal be isolated to its own session when a workspace runs several concurrent
/// sessions (the cross-session leak, threat T5/T3).
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
///   The non-unique composite index on (<c>session_id</c>, <c>resource_type</c>,
///   <c>resource_id</c>) is the documented critical index
///   <c>visibility_rules(session_id, resource_type, resource_id)</c>
///   (docs/10_DATABASE_SCHEMA.md): a reveal is session-scoped (CORE-SVIS-001), so "find the rule(s) for
///   this resource in this session" leads with the session column — the lookup the session-scoped
///   policy and the reveal command perform. It is NON-unique because it spans BOTH dimensions: a
///   resource carries at most the audience-wide rule plus one rule per selected participant within a
///   session (CORE-VIS-005), so this read index returns the whole rule set for a resource regardless of
///   dimension.
///   </item>
///   <item>
///   Two FILTERED (partial) UNIQUE indexes enforce the single-rule-per-(session, resource, dimension)
///   constraint (CORE-SVIS-002), so a resource can never accumulate two rules in the same dimension —
///   the ghost-reveal race where two concurrent first-reveals each insert a visible rule and a later hide
///   flips only one, leaving the other Visible as an un-hideable ghost (threats T5/T3). The dimension is
///   either AUDIENCE-WIDE (<c>target_participant_id IS NULL</c>) or one SELECTED participant
///   (<c>target_participant_id IS NOT NULL</c>). Because <c>target_participant_id</c> is nullable and
///   BOTH PostgreSQL and SQLite treat NULLs as DISTINCT in a unique index, a single unique index over
///   (<c>session_id</c>, <c>resource_type</c>, <c>resource_id</c>, <c>target_participant_id</c>) would
///   NOT reject a second audience-wide rule (two NULL targets compare distinct). So uniqueness is
///   expressed as two partial indexes — the same nullable-uniqueness pattern as
///   <c>templates(organization_id IS NULL / IS NOT NULL)</c>: one over (<c>session_id</c>,
///   <c>resource_type</c>, <c>resource_id</c>) filtered to <c>target_participant_id IS NULL</c> (at most
///   one audience-wide rule), and one over the four columns filtered to
///   <c>target_participant_id IS NOT NULL</c> (at most one rule per selected participant; two reveals to
///   DIFFERENT participants stay independent dimensions). The reveal command relies on these via
///   insert-on-conflict (a losing concurrent first-create is reported as a duplicate and converges onto
///   the one rule rather than creating a second; <see cref="RevealService"/>).
///   </item>
///   <item>
///   The non-unique composite index on (<c>organization_id</c>, <c>workspace_id</c>) keeps the
///   tenant boundary check (checked before the workspace boundary) and the organization foreign-key
///   check efficient and makes tenant-scoped reads lead with <c>organization_id</c> (threat T5). It
///   also backs the workspace-wide candidate read (the participant feed) and the role-level
///   workspace-wide visibility read (asset download), which lead with the tenant then workspace.
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

        // Session boundary (CORE-SVIS-001): a reveal is session-scoped, so the rule carries the session
        // it belongs to as a first-class, required column. A workspace may run several concurrent
        // sessions, so this column is what isolates a reveal to its own session (the cross-session leak,
        // threat T5/T3).
        builder.Property(rule => rule.SessionId)
            .HasColumnName("session_id")
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

        // Documented critical index visibility_rules(session_id, resource_type, resource_id)
        // (CORE-SVIS-001; docs/10_DATABASE_SCHEMA.md): a reveal is session-scoped, so "find the rule(s)
        // for this resource in this session" leads with the session column — the lookup the
        // session-scoped policy and the reveal command perform. NON-unique because it spans BOTH
        // dimensions (a resource carries at most the audience-wide rule plus one rule per selected
        // participant within a session, CORE-VIS-005), so this read returns the whole rule set for a
        // resource regardless of dimension. The single-rule-per-dimension UNIQUENESS is enforced by the
        // two filtered indexes below (CORE-SVIS-002).
        builder.HasIndex(rule => new { rule.SessionId, rule.ResourceType, rule.ResourceId })
            .HasDatabaseName("ix_visibility_rules_session_id_resource_type_resource_id");

        // SINGLE-RULE-PER-DIMENSION uniqueness (CORE-SVIS-002): at most ONE active visibility rule per
        // (session, resource, dimension). The dimension is audience-wide (target_participant_id IS NULL)
        // or one selected participant (target_participant_id IS NOT NULL). target_participant_id is
        // nullable and both PostgreSQL and SQLite treat NULLs as DISTINCT in a unique index, so a single
        // unique index on the four columns would not reject a second audience-wide rule (two NULL targets
        // compare distinct) — exactly the ghost-reveal race this story closes. Express it as two FILTERED
        // unique indexes (the templates(organization_id IS NULL / IS NOT NULL) pattern). The filter SQL is
        // portable across both providers (plain column name + IS [NOT] NULL), so EnsureCreated builds it
        // for the SQLite test schema and the migration builds it for PostgreSQL identically.

        // Audience-wide dimension: at most one rule per (session, resource) with NO participant target.
        // The named-index overload keeps this DISTINCT from the non-unique read index above (same columns):
        // without a distinguishing name EF would merge the two HasIndex calls into one and drop the
        // documented critical read index.
        builder.HasIndex(
                rule => new { rule.SessionId, rule.ResourceType, rule.ResourceId },
                "ix_visibility_rules_session_resource_audience_dimension")
            .IsUnique()
            .HasFilter("\"target_participant_id\" IS NULL");

        // Selected-participant dimension: at most one rule per (session, resource, participant). Two
        // reveals to DIFFERENT participants are different dimensions and remain independent.
        builder.HasIndex(rule => new { rule.SessionId, rule.ResourceType, rule.ResourceId, rule.TargetParticipantId })
            .IsUnique()
            .HasFilter("\"target_participant_id\" IS NOT NULL")
            .HasDatabaseName("ix_visibility_rules_session_resource_participant_dimension");

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

        // Session foreign key (CORE-SVIS-001): every visibility rule hangs off exactly one session — the
        // session the reveal happened in. Cascade delete removes a session's rules with it, mirroring the
        // organization and workspace cascades; a rule has no meaning without its session.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(rule => rule.SessionId)
            .HasConstraintName("fk_visibility_rules_sessions_session_id")
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
