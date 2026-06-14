using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Relational mapping of <see cref="SessionEvent"/> to the append-only <c>session_events</c> table
/// (CORE-RT-003; csv/database_tables.csv: module Realtime, scope <c>session</c>, "Append-only event
/// stream"). The event is session-scoped, so it carries <c>organization_id</c> (the tenant),
/// <c>workspace_id</c> and <c>session_id</c> columns (docs/10_DATABASE_SCHEMA.md principle:
/// tenant/workspace/session-scoped tables include the matching id; threat T5).
///
/// The single documented critical index is <c>session_events(session_id, created_at, event_id)</c>
/// (docs/10_DATABASE_SCHEMA.md): the stream is read per session in append order, so the composite index
/// leads with the session column, then the timestamp, then the time-ordered event id. It is NON-unique.
///
/// All three reference columns are foreign keys that CASCADE on delete: an event has no meaning without
/// its session/workspace/tenant, so removing any of them removes the session's events (mirrors the
/// required cascades on the other session/workspace-scoped tables). Unlike the Audit module's
/// <c>audit_logs</c> — a long-retained security log whose references are recorded facts — the session
/// event stream is operational live-session state, so it is coupled to and removed with its session.
///
/// REAL COLUMNS, NO JSON for the addressing fields: the event type is a string column, the recipient
/// routing (<c>target_participant_id</c>) is a real nullable column, and the visibility subject
/// (<c>visibility_subject_type</c> + <c>visibility_subject_id</c>, CORE-RT-004) is a real nullable
/// (type-name, id) pair — none of these is buried in the payload. Only the server-composed <c>payload</c>
/// is free-form JSON text — resource identifiers, never resolved content (threat T7). The
/// <c>created_by</c>, <c>target_participant_id</c> and <c>visibility_subject_id</c> columns are recorded
/// references and are intentionally NOT foreign keys (an event is an immutable historical fact that must
/// survive a later user/participant/resource deletion, and <c>visibility_subject_id</c> is polymorphic
/// across three resource tables — mirroring <c>visibility_rules.resource_id</c> and the audit log's
/// recorded references).
/// </summary>
internal sealed class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
    public void Configure(EntityTypeBuilder<SessionEvent> builder)
    {
        builder.ToTable("session_events");

        builder.HasKey(sessionEvent => sessionEvent.Id);

        builder.Property(sessionEvent => sessionEvent.Id)
            .HasColumnName("event_id")
            .ValueGeneratedNever();

        // The per-session monotonic sequence (CORE-RTC-001): the authoritative ordering/replay key, allocated
        // by the append path and never database-generated.
        builder.Property(sessionEvent => sessionEvent.Sequence)
            .HasColumnName("sequence")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.OrganizationId)
            .HasColumnName("organization_id")
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.WorkspaceId)
            .HasColumnName("workspace_id")
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.SessionId)
            .HasColumnName("session_id")
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(SessionEvent.MaxEventTypeLength)
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.CreatedBy)
            .HasColumnName("created_by");

        builder.Property(sessionEvent => sessionEvent.TargetParticipantId)
            .HasColumnName("target_participant_id");

        builder.Property(sessionEvent => sessionEvent.Payload)
            .HasColumnName("payload")
            .HasMaxLength(SessionEvent.MaxPayloadLength)
            .IsRequired();

        // Visibility subject (CORE-RT-004): the resource whose audience visibility gates who may receive
        // the event — a REAL nullable (type-name string, id) pair, not buried in the payload. The type is
        // a string so the Realtime module stays decoupled from the Visibility enum, and the id is a
        // polymorphic, non-FK reference (see the column note below). Both are nullable: an event either
        // carries a subject (both set) or none (both null).
        builder.Property(sessionEvent => sessionEvent.VisibilitySubjectType)
            .HasColumnName("visibility_subject_type")
            .HasMaxLength(SessionEvent.MaxVisibilitySubjectTypeLength);

        builder.Property(sessionEvent => sessionEvent.VisibilitySubjectId)
            .HasColumnName("visibility_subject_id");

        builder.Property(sessionEvent => sessionEvent.SchemaVersion)
            .HasColumnName("schema_version")
            .IsRequired();

        builder.Property(sessionEvent => sessionEvent.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        // Authoritative ordering/replay index session_events(session_id, sequence) UNIQUE (CORE-RTC-001):
        // the stream is read and replayed per session by the gap-free monotonic sequence, not the
        // millisecond-resolution event id. UNIQUE is the integrity backstop that guarantees no two events of
        // a session ever share a sequence (no duplicate, no silent gap), independent of the allocator.
        builder.HasIndex(sessionEvent => new { sessionEvent.SessionId, sessionEvent.Sequence })
            .IsUnique()
            .HasDatabaseName("ix_session_events_session_id_sequence");

        // Retained critical index session_events(session_id, created_at, event_id): created_at still backs
        // the documented time-range replay (docs/10_DATABASE_SCHEMA.md). NON-unique.
        builder.HasIndex(sessionEvent => new { sessionEvent.SessionId, sessionEvent.CreatedAt, sessionEvent.Id })
            .HasDatabaseName("ix_session_events_session_id_created_at_event_id");

        // Tenant foreign key: cascade delete removes a tenant's events with the tenant.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(sessionEvent => sessionEvent.OrganizationId)
            .HasConstraintName("fk_session_events_organizations_organization_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace foreign key: cascade delete removes events with their workspace.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(sessionEvent => sessionEvent.WorkspaceId)
            .HasConstraintName("fk_session_events_workspaces_workspace_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Session foreign key: the event belongs to one session's stream; cascade delete removes the
        // stream with the session.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(sessionEvent => sessionEvent.SessionId)
            .HasConstraintName("fk_session_events_sessions_session_id")
            .OnDelete(DeleteBehavior.Cascade);

        // NOTE: created_by and target_participant_id are deliberately NOT foreign keys — an appended
        // event is an immutable historical fact that must survive later deletion of the user/participant
        // it references (mirrors visibility_rules.resource_id and the audit log's recorded references).
    }
}
