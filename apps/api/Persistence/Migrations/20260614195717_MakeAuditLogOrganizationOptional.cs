using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Makes <c>audit_logs.organization_id</c> NULLABLE so the append-only audit log can record PLATFORM-LEVEL audit
/// facts (CORE-SPEC-002). A purchase and the entitlement it grants are deployment-spanning, NOT tenant-scoped — a
/// user's premium state follows the user's purchase, not an organization, and a purchase is named globally with no
/// organization (docs/21) — so the entitlement grant/revocation, the purchase verification and the store-notification
/// audit facts that back the (audit=true) rows of <c>csv/entitlement_event_catalog.csv</c> carry a null organization,
/// distinct from the tenant-scoped facts (a visibility change, a session lifecycle transition, a quota denial) that
/// keep their organization.
///
/// The tenant foreign key into <c>organizations</c> stays — now OPTIONAL — so a SET organization is still enforced at
/// the row level (threat T5) and a tenant teardown still cascades its own audit log; a platform fact simply has no
/// tenant key. A platform fact stands OUTSIDE the per-tenant tamper-evident hash chain (CORE-SEC-003), whose spine is
/// the per-tenant append sequence, so it is appended unsequenced (sequence 0, null hashes) exactly like the
/// established append-only <c>purchase_events</c> monetization trail is recorded; the unique index
/// <c>audit_logs(organization_id, sequence)</c> is unchanged and the tenant-scoped reads filter by a concrete
/// organization, so a platform fact never appears in any tenant's chain or query (threat T5).
///
/// Rollback: <see cref="Down"/> makes the column NOT NULL again (roll-forward-only policy, CORE-DR-004; the down is
/// never run in production).
/// </summary>
public partial class MakeAuditLogOrganizationOptional : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "organization_id",
            table: "audit_logs",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "organization_id",
            table: "audit_logs",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
    }
}
