// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Audit;

/// <summary>
/// Relational mapping of <see cref="AuditLogSequence"/> to the <c>audit_log_sequences</c> table (CORE-SEC-003):
/// the per-tenant counter that allocates the gap-free, strictly monotonic APPEND sequence numbers of the
/// append-only <c>audit_logs</c> hash chain. One row per organization, keyed by <c>organization_id</c>. It is
/// the audit analogue of the Realtime <c>session_event_sequences</c> mapping (CORE-RTC-001), scoped to the
/// tenant instead of the session.
///
/// The <c>organization_id</c> primary key is also a foreign key into <c>organizations(id)</c> that CASCADES on
/// delete: the counter has no meaning without its tenant, so it is removed with the tenant exactly as the audit
/// log itself is (the tenant teardown removes its whole audit trail and the counter that sequences it). The
/// single <c>last_sequence</c> column holds the highest number handed out; it is advanced only by the append
/// path's atomic <c>INSERT ... ON CONFLICT DO UPDATE</c> (<see cref="AuditLogRepository.AppendAsync"/>), never
/// by a tracked write.
/// </summary>
internal sealed class AuditLogSequenceConfiguration : IEntityTypeConfiguration<AuditLogSequence>
{
    public void Configure(EntityTypeBuilder<AuditLogSequence> builder)
    {
        builder.ToTable("audit_log_sequences");

        builder.HasKey(counter => counter.OrganizationId);

        builder.Property(counter => counter.OrganizationId)
            .HasColumnName("organization_id")
            .ValueGeneratedNever();

        builder.Property(counter => counter.LastSequence)
            .HasColumnName("last_sequence")
            .IsRequired();

        // The tenant owns its counter: cascade delete removes the counter with the organization (mirrors the
        // audit_logs tenant foreign key). The counter is keyed by the tenant, so the key IS the foreign key.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(counter => counter.OrganizationId)
            .HasConstraintName("fk_audit_log_sequences_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
