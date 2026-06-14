using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Makes the append-only <c>audit_logs</c> table TAMPER-EVIDENT (CORE-SEC-003, the "Security Hardening" epic).
/// The log was already application-level append-only (immutable aggregate, append + read repository, no
/// update/delete), but nothing detected a DB-level actor or a future regression that altered or deleted a
/// persisted row directly. This adds a per-tenant hash chain: every entry gains a <c>sequence</c> (a per-tenant,
/// gap-free, strictly monotonic APPEND number — the chain's spine), a <c>previous_hash</c> (the link to the
/// preceding entry, null for a tenant's genesis entry) and an <c>entry_hash</c> (a SHA-256 over the entry's
/// recorded fields and previous hash). Changing, deleting or reordering a row breaks the chain and the
/// verification routine (<c>AuditLogChainVerifier</c>) reports it. The numbers are handed out by a new
/// <c>audit_log_sequences</c> counter table (one row per tenant) the append path increments atomically — the
/// audit analogue of <c>session_event_sequences</c> (CORE-RTC-001), scoped to the tenant.
///
/// EXISTING ROWS ARE BACKFILLED so the new <c>sequence</c> column is correct and the unique index can be created
/// without a collision: each tenant's entries are numbered 1.. in their historical append order
/// (<c>created_at</c> then <c>id</c>) before the column is made <c>NOT NULL</c>, and the counter table is then
/// seeded from each tenant's highest sequence so the next appended entry continues the run. The <c>previous_hash</c>
/// and <c>entry_hash</c> columns stay nullable and existing rows keep them NULL: a SHA-256 cannot be computed in
/// portable migration SQL, so pre-existing rows are LEGACY (their integrity predates the chain) and are not
/// verified; the first entry appended after this migration starts a fresh genesis. The unique index
/// <c>audit_logs(organization_id, sequence)</c> is the integrity backstop guaranteeing no two entries of a tenant
/// ever share a sequence (so the chain stays a single linear spine).
///
/// Rollback: <see cref="Down"/> drops the unique index, the counter table and the three columns.
/// </summary>
public partial class AddAuditLogTamperEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The chain hashes (this entry's hash and the link to its predecessor). Nullable: a SHA-256 cannot be
        // computed in portable migration SQL, so existing rows stay NULL (legacy, pre-chain); every entry
        // appended through the sealed append path carries a non-null entry_hash.
        migrationBuilder.AddColumn<string>(
            name: "entry_hash",
            table: "audit_logs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "previous_hash",
            table: "audit_logs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        // Add the sequence nullable first so existing rows can be backfilled before the NOT NULL constraint
        // (a flat default would make every existing row of a tenant share sequence 0 and fail the unique index).
        migrationBuilder.AddColumn<long>(
            name: "sequence",
            table: "audit_logs",
            type: "bigint",
            nullable: true);

        // Backfill: number each tenant's entries 1.. in their historical append order (created_at, then the
        // time-ordered id as a stable tiebreak within a millisecond).
        migrationBuilder.Sql(@"
            UPDATE audit_logs AS a
            SET sequence = ordered.rn
            FROM (
                SELECT id,
                       ROW_NUMBER() OVER (PARTITION BY organization_id ORDER BY created_at, id) AS rn
                FROM audit_logs
            ) AS ordered
            WHERE a.id = ordered.id;");

        // Every row now has a value, so enforce NOT NULL.
        migrationBuilder.AlterColumn<long>(
            name: "sequence",
            table: "audit_logs",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true);

        migrationBuilder.CreateTable(
            name: "audit_log_sequences",
            columns: table => new
            {
                organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                last_sequence = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_log_sequences", x => x.organization_id);
                table.ForeignKey(
                    name: "fk_audit_log_sequences_organizations_organization_id",
                    column: x => x.organization_id,
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        // Seed the allocator from the backfilled log so the next appended entry continues each tenant's run
        // rather than restarting at 1.
        migrationBuilder.Sql(@"
            INSERT INTO audit_log_sequences (organization_id, last_sequence)
            SELECT organization_id, MAX(sequence)
            FROM audit_logs
            GROUP BY organization_id;");

        migrationBuilder.CreateIndex(
            name: "ix_audit_logs_organization_id_sequence",
            table: "audit_logs",
            columns: new[] { "organization_id", "sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_log_sequences");

        migrationBuilder.DropIndex(
            name: "ix_audit_logs_organization_id_sequence",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "entry_hash",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "previous_hash",
            table: "audit_logs");

        migrationBuilder.DropColumn(
            name: "sequence",
            table: "audit_logs");
    }
}
