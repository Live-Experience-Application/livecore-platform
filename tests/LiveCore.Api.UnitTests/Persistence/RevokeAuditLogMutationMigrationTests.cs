// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Asserts that the checked-in <see cref="RevokeAuditLogMutationFromRuntimeRole"/> migration (CORE-SEC-004)
/// actually issues the database-level prevention — a <c>REVOKE UPDATE, DELETE ON audit_logs</c> from the runtime
/// application role — rather than the previously documentation-only step. The migration's SQL is PostgreSQL and
/// runs only against PostgreSQL in the deployment/integration pipeline, so this unit test inspects the migration's
/// built operations directly (no database needed) to prove the REVOKE ships with the schema.
/// </summary>
public sealed class RevokeAuditLogMutationMigrationTests
{
    private static string UpSql()
    {
        var migration = new RevokeAuditLogMutationFromRuntimeRole();
        var sql = migration.UpOperations.OfType<SqlOperation>().Single().Sql;
        return sql;
    }

    [Fact]
    public void The_up_migration_revokes_update_and_delete_on_audit_logs()
    {
        var sql = UpSql();

        Assert.Contains("REVOKE UPDATE, DELETE ON TABLE audit_logs", sql);
        // The application still appends and reads; those grants are preserved.
        Assert.Contains("GRANT INSERT, SELECT ON TABLE audit_logs", sql);
    }

    [Fact]
    public void The_up_migration_targets_the_configured_runtime_role_and_no_ops_when_unset()
    {
        var sql = UpSql();

        // The role is read from the deployment-configured database setting, and the REVOKE only runs when it is
        // set — so applying migrations on a single-role dev/CI database is a safe no-op (never fails on a missing
        // role). The role is interpolated through format(... %I ...), a safely-quoted identifier.
        Assert.Contains("livecore.audit_log_app_role", sql);
        Assert.Contains("IF app_role IS NOT NULL", sql);
        Assert.Contains("%I", sql);
    }

    [Fact]
    public void The_down_migration_grants_mutation_back_to_the_configured_role()
    {
        var migration = new RevokeAuditLogMutationFromRuntimeRole();
        var sql = migration.DownOperations.OfType<SqlOperation>().Single().Sql;

        Assert.Contains("GRANT UPDATE, DELETE ON TABLE audit_logs", sql);
        Assert.Contains("livecore.audit_log_app_role", sql);
    }
}
