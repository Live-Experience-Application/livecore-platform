using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Adds the per-session monotonic event sequence (CORE-RTC-001). Every session event gains a
/// <c>sequence</c> column — a per-session, gap-free, strictly monotonic number — and the stream is ordered
/// and replayed by <c>session_events(session_id, sequence)</c> rather than the UUIDv7 <c>event_id</c>, which
/// is only monotonic at millisecond resolution and so reorders events appended within one millisecond (a
/// single reveal publishes several at the same instant). The numbers are handed out by a new
/// <c>session_event_sequences</c> counter table (one row per session) the append path increments atomically.
///
/// EXISTING ROWS ARE BACKFILLED so the new column is correct and the unique index can be created without a
/// collision: each session's events are numbered 1.. in their historical append order (<c>created_at</c>
/// then <c>event_id</c>) before the column is made <c>NOT NULL</c>, and the counter table is then seeded
/// from each session's highest sequence so the next appended event continues the run. The unique index
/// <c>session_events(session_id, sequence)</c> is the integrity backstop guaranteeing no two events of a
/// session ever share a sequence.
///
/// Rollback: <see cref="Down"/> drops the unique index, the counter table and the column.
/// </summary>
public partial class AddSessionEventSequence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add the column nullable first so existing rows can be backfilled before the NOT NULL constraint
        // (a flat default would make every existing row share sequence 0 and fail the unique index).
        migrationBuilder.AddColumn<long>(
            name: "sequence",
            table: "session_events",
            type: "bigint",
            nullable: true);

        // Backfill: number each session's events 1.. in their historical append order (created_at, then the
        // time-ordered event_id as a stable tiebreak within a millisecond).
        migrationBuilder.Sql(@"
            UPDATE session_events AS e
            SET sequence = ordered.rn
            FROM (
                SELECT event_id,
                       ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at, event_id) AS rn
                FROM session_events
            ) AS ordered
            WHERE e.event_id = ordered.event_id;");

        // Every row now has a value, so enforce NOT NULL.
        migrationBuilder.AlterColumn<long>(
            name: "sequence",
            table: "session_events",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true);

        migrationBuilder.CreateTable(
            name: "session_event_sequences",
            columns: table => new
            {
                session_id = table.Column<Guid>(type: "uuid", nullable: false),
                last_sequence = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_session_event_sequences", x => x.session_id);
                table.ForeignKey(
                    name: "fk_session_event_sequences_sessions_session_id",
                    column: x => x.session_id,
                    principalTable: "sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Seed the allocator from the backfilled stream so the next appended event continues each session's
        // run rather than restarting at 1.
        migrationBuilder.Sql(@"
            INSERT INTO session_event_sequences (session_id, last_sequence)
            SELECT session_id, MAX(sequence)
            FROM session_events
            GROUP BY session_id;");

        migrationBuilder.CreateIndex(
            name: "ix_session_events_session_id_sequence",
            table: "session_events",
            columns: new[] { "session_id", "sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_session_events_session_id_sequence",
            table: "session_events");

        migrationBuilder.DropTable(
            name: "session_event_sequences");

        migrationBuilder.DropColumn(
            name: "sequence",
            table: "session_events");
    }
}
