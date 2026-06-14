using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Audit;

/// <summary>
/// Relational mapping of <see cref="AuditLogEntry"/> to the append-only <c>audit_logs</c> table
/// (CORE-VIS-006; csv/database_tables.csv: module Audit, scope <c>organization</c>, "Append-only
/// audit"). The log is tenant-scoped, so it carries an <c>organization_id</c> column
/// (docs/10_DATABASE_SCHEMA.md principle: "tenant-scoped tables include <c>organization_id</c>").
///
/// The single documented critical index is <c>audit_logs(organization_id, created_at)</c>
/// (docs/10_DATABASE_SCHEMA.md): the audit log is read per tenant in time order, so the composite index
/// leads with the tenant column and then the timestamp. It is NON-unique — a tenant has many entries.
///
/// FOREIGN KEYS — ONLY THE TENANT. <c>organization_id</c> is a foreign key into <c>organizations(id)</c>
/// so tenant isolation is enforced at the row level (threat T5 in docs/07_SECURITY_THREAT_MODEL.md); it
/// CASCADES on delete because a tenant teardown removes the whole tenant including its audit log. The
/// other reference columns — <c>workspace_id</c>, <c>actor_user_profile_id</c>, <c>resource_id</c>,
/// <c>target_participant_id</c> — are deliberately NOT foreign keys: an append-only audit trail must
/// SURVIVE the later deletion of the workspace, user, resource or participant it references and must
/// never be cascade-erased, so it cannot be coupled by referential integrity to mutable business rows.
/// They are recorded facts (mirrors the polymorphic, non-foreign-key <c>visibility_rules.resource_id</c>).
///
/// REAL COLUMNS, NO JSON. The action, the optional resource-type name and the before/after state names
/// are first-class string columns, persisted by their stable NAME exactly like <c>visibility</c> on
/// <c>visibility_rules</c> and <c>content_type</c> on <c>content_blocks</c> — never inside arbitrary
/// JSON (docs/10_DATABASE_SCHEMA.md: JSONB is "not for core authorization fields", and an audit record
/// is a security record). No free-form scene/content body is ever stored (threat T7).
/// </summary>
internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(entry => entry.WorkspaceId)
            .HasColumnName("workspace_id");

        builder.Property(entry => entry.Action)
            .HasColumnName("action")
            // Persist the action as its stable name (not its numeric value), mirrors the other enums.
            .HasConversion<string>()
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.ActorUserProfileId)
            .HasColumnName("actor_user_profile_id");

        builder.Property(entry => entry.ResourceType)
            .HasColumnName("resource_type")
            .HasMaxLength(AuditLogEntry.MaxResourceTypeLength);

        builder.Property(entry => entry.ResourceId)
            .HasColumnName("resource_id");

        builder.Property(entry => entry.TargetParticipantId)
            .HasColumnName("target_participant_id");

        builder.Property(entry => entry.PreviousState)
            .HasColumnName("previous_state")
            .HasMaxLength(AuditLogEntry.MaxStateLength);

        // Optional: a generic action need not be a state transition (CORE-AUD-001). The visibility
        // producer always writes a new state, but a session start or a member invite records none.
        builder.Property(entry => entry.NewState)
            .HasColumnName("new_state")
            .HasMaxLength(AuditLogEntry.MaxStateLength);

        builder.Property(entry => entry.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // TAMPER-EVIDENT HASH CHAIN (CORE-SEC-003). The per-tenant append sequence is the spine the chain is
        // linked along: gap-free and strictly monotonic per tenant, handed out by the audit_log_sequences
        // allocator. It is REQUIRED — every appended entry is sealed with one — and distinct from the
        // event-time-ordered id (the chain follows append order, not event time).
        builder.Property(entry => entry.Sequence)
            .HasColumnName("sequence")
            .IsRequired();

        // The link to the preceding entry's hash; null only for a tenant's genesis entry.
        builder.Property(entry => entry.PreviousHash)
            .HasColumnName("previous_hash")
            .HasMaxLength(AuditLogChain.HashHexLength);

        // This entry's own hash. Nullable so a legacy row written before this hardening (with no hash) round-
        // trips; every entry appended through the sealed append path carries a non-null hash.
        builder.Property(entry => entry.EntryHash)
            .HasColumnName("entry_hash")
            .HasMaxLength(AuditLogChain.HashHexLength);

        // Documented critical index audit_logs(organization_id, created_at): reads lead with the
        // tenant column and then the timestamp (docs/10_DATABASE_SCHEMA.md). NON-unique.
        builder.HasIndex(entry => new { entry.OrganizationId, entry.CreatedAt })
            .HasDatabaseName("ix_audit_logs_organization_id_created_at");

        // Integrity backstop for the hash chain (CORE-SEC-003): the per-tenant append sequence is UNIQUE, so no
        // two entries of a tenant can ever share a sequence and the chain stays a single linear spine
        // (mirrors session_events(session_id, sequence) unique, CORE-RTC-001).
        builder.HasIndex(entry => new { entry.OrganizationId, entry.Sequence })
            .HasDatabaseName("ix_audit_logs_organization_id_sequence")
            .IsUnique();

        // Tenant foreign key: every audit entry hangs off exactly one organization. Cascade delete
        // removes a tenant's audit log only with the tenant itself; no finer-grained reference cascades
        // (see the type summary), so the trail survives workspace/user/resource deletion.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(entry => entry.OrganizationId)
            .HasConstraintName("fk_audit_logs_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
