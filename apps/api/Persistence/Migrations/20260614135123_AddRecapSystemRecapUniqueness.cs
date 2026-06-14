using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Enforces AT MOST ONE SYSTEM recap per session on the <c>recaps</c> table (CORE-RCP-001;
/// docs/10_DATABASE_SCHEMA.md). The background recap generation job runs an eligibility NOT EXISTS read that
/// is decoupled from the bare insert and can run in any number of worker replicas with overlapping sweeps
/// (none has a single-instance guard), and each system recap mints a fresh UUIDv7 primary key — so before this,
/// two concurrent sweeps both inserted and a session ended up with duplicate system recaps (the only existing
/// <c>recaps</c> indexes were non-unique).
///
/// Because <c>generated_by</c> is nullable and a recap may be produced by the SYSTEM (<c>generated_by IS
/// NULL</c>) or by a HOST (<c>generated_by IS NOT NULL</c>, of which a session may legitimately have many), the
/// at-most-one rule applies only to system recaps. It is expressed as a single FILTERED (partial) unique index
/// over <c>session_id</c> filtered to <c>generated_by IS NULL</c> — the same nullable-uniqueness pattern as the
/// <c>visibility_rules</c> per-dimension uniqueness (CORE-SVIS-002) and <c>templates(organization_id)</c>. The
/// generation job relies on it via insert-on-conflict: a losing concurrent append is rejected and converges
/// onto the one recap rather than producing a duplicate, so "a second concurrent sweep produces no duplicate".
///
/// The filter SQL is portable (plain column name + IS NULL), so EnsureCreated builds it for the SQLite test
/// schema and this migration builds it for PostgreSQL identically. This migration creates only an index (no
/// data or column change) and is reversible. It assumes the table holds no pre-existing duplicate system recap
/// for a session; the recap-generation job is the only writer of system recaps and a v1 deployment runs it on a
/// single worker, so there is none to conflict with.
/// </summary>
public partial class AddRecapSystemRecapUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_recaps_session_id_system_recap",
            table: "recaps",
            column: "session_id",
            unique: true,
            filter: "\"generated_by\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_recaps_session_id_system_recap",
            table: "recaps");
    }
}
