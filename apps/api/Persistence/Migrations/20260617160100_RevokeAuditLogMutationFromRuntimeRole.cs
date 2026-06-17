using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LiveCore.Api.Persistence.Migrations;

/// <summary>
/// Makes the append-only <c>audit_logs</c> table TAMPER-PROOF at the database, not only tamper-evident
/// (CORE-SEC-004, the "Audit Integrity and Security Hardening" epic). CORE-SEC-003 sealed every entry into a
/// per-tenant SHA-256 hash chain so altering or deleting a persisted row is DETECTABLE, and
/// docs/13_SELF_HOSTING_REQUIREMENTS.md documented the matching prevention — REVOKE UPDATE/DELETE on
/// <c>audit_logs</c> from the runtime application role — as an operator step. This migration turns that
/// documented step into a checked-in, automated one so the prevention ships with the schema rather than relying
/// on every operator remembering it.
///
/// CONFIGURED, FAIL-SAFE ROLE. The runtime application role's name is deployment-specific, so the migration does
/// not hardcode one: it reads the role from the custom database setting <c>livecore.audit_log_app_role</c> (an
/// operator sets it once, e.g. <c>ALTER DATABASE livecore SET livecore.audit_log_app_role = 'livecore_app';</c>,
/// see docs/13_SELF_HOSTING_REQUIREMENTS.md). When the setting is present the migration REVOKEs <c>UPDATE</c> and
/// <c>DELETE</c> on <c>audit_logs</c> from that role (and re-GRANTs the <c>INSERT</c>/<c>SELECT</c> the
/// application still needs); when it is unset — a single-role dev/CI database, or an operator who has not opted in
/// — the migration is a safe NO-OP, so applying migrations never fails on a missing role. The role name is
/// interpolated through <c>format(... %I ...)</c>, so it is a safely-quoted identifier and not an injection
/// surface.
///
/// LAYERING. This is the DB-level prevention; it is paired in code with the non-tracked audit reads and the
/// <c>AuditLogTamperProtectionInterceptor</c> (which block an in-process UPDATE/DELETE fail-closed) and with the
/// hash chain (which DETECTS any tampering that bypasses all of them). The migration runner is the separate,
/// more-privileged role (docs/13), so this REVOKE on the application role never interferes with applying
/// migrations or with a tenant-teardown cascade.
///
/// Rollback: <see cref="Down"/> re-GRANTs <c>UPDATE</c>/<c>DELETE</c> to the configured role (also a no-op when
/// the setting is unset). It issues no DropTable/DropColumn, so it is not a data-destroying Down().
/// </summary>
public partial class RevokeAuditLogMutationFromRuntimeRole : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Run only when the operator has named the runtime application role via the
        // livecore.audit_log_app_role database setting; otherwise do nothing (a single-role dev/CI database has
        // no separate least-privilege role to revoke from). format(... %I ...) safely quotes the role identifier.
        migrationBuilder.Sql(@"
            DO $$
            DECLARE
                app_role text := current_setting('livecore.audit_log_app_role', true);
            BEGIN
                IF app_role IS NOT NULL AND btrim(app_role) <> '' THEN
                    EXECUTE format('REVOKE UPDATE, DELETE ON TABLE audit_logs FROM %I', app_role);
                    -- The application still appends (INSERT) and reads (SELECT) the audit log; keep those.
                    EXECUTE format('GRANT INSERT, SELECT ON TABLE audit_logs TO %I', app_role);
                END IF;
            END $$;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverse the prevention: grant UPDATE/DELETE back to the configured role (no-op when unset). Roll-forward
        // is the production policy (docs/13); this exists for completeness and is non-destructive (no Drop).
        migrationBuilder.Sql(@"
            DO $$
            DECLARE
                app_role text := current_setting('livecore.audit_log_app_role', true);
            BEGIN
                IF app_role IS NOT NULL AND btrim(app_role) <> '' THEN
                    EXECUTE format('GRANT UPDATE, DELETE ON TABLE audit_logs TO %I', app_role);
                END IF;
            END $$;");
    }
}
