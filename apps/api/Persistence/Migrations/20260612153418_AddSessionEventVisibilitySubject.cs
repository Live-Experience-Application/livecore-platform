using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the visibility subject to the <c>session_events</c> table (CORE-RT-004, recipient-specific event
/// projection). Two new NULLABLE columns record the Core resource whose audience visibility GATES who may
/// receive an event — <c>visibility_subject_type</c> (the generic resource-kind name, e.g. <c>Entity</c>)
/// and <c>visibility_subject_id</c> — the documented <c>visibilityProjection</c> input
/// (docs/09_EVENT_CATALOG.md). They let the Realtime delivery compute recipients per-recipient through
/// the central Visibility engine, so realtime delivery never leaks a hidden event (threat T3 in
/// docs/07_SECURITY_THREAT_MODEL.md; docs/11_REALTIME_SYNC.md).
///
/// Both are REAL columns (never JSON), nullable, and travel TOGETHER: an event either carries a subject
/// (both set) or none (both null, an unconditional audience event). The type is a string so the Realtime
/// module stays decoupled from the Visibility resource-type enum (mirroring the audit log's recorded
/// resource type). <c>visibility_subject_id</c> is a polymorphic reference across the
/// <c>scenes</c>/<c>content_blocks</c>/<c>entities</c> tables and is intentionally NOT a foreign key —
/// like <c>visibility_rules.resource_id</c> and the event's other recorded references, an appended event
/// is an immutable historical fact that must survive a later deletion of the resource it references.
/// Existing rows keep NULL subjects and are unaffected. Rollback: <see cref="Down"/> drops both columns.
/// </summary>
public partial class AddSessionEventVisibilitySubject : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "visibility_subject_type",
            table: "session_events",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "visibility_subject_id",
            table: "session_events",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "visibility_subject_type",
            table: "session_events");

        migrationBuilder.DropColumn(
            name: "visibility_subject_id",
            table: "session_events");
    }
}
