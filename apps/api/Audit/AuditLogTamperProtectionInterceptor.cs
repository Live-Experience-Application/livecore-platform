// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LiveCore.Api.Audit;

/// <summary>
/// The in-process TAMPER-PROOFING control over the append-only audit log (CORE-SEC-004). The log was already
/// application-level append-only (an immutable <see cref="AuditLogEntry"/>, an append + read repository with no
/// update/delete path) and tamper-EVIDENT (the per-tenant SHA-256 hash chain, CORE-SEC-003) — but a regression
/// inside the running process (a read path that forgot <c>AsNoTracking</c>, new code that re-points the table)
/// could still track an audit row and have <c>SaveChanges</c> issue an UPDATE or DELETE against it. This
/// EF Core <see cref="SaveChangesInterceptor"/> closes that gap: on every <c>SaveChanges</c> it inspects the
/// change tracker and, if any <see cref="AuditLogEntry"/> is in <see cref="EntityState.Modified"/> or
/// <see cref="EntityState.Deleted"/>, it throws <see cref="AuditLogTamperException"/> and FAILS CLOSED — the
/// whole save is aborted before it reaches the database, so the mutation never persists.
///
/// ONLY UPDATE/DELETE ARE BLOCKED. <see cref="EntityState.Added"/> is the audit log's one legitimate write — the
/// append path (<see cref="AuditLogRepository.AppendAsync"/>) inserts a freshly sealed entry — so it is allowed.
/// <see cref="EntityState.Unchanged"/> and a detached (non-tracked) entity are not writes and are ignored, so the
/// non-tracked reads (CORE-SEC-004) and the sealed append pass through untouched. The interceptor is provider-
/// agnostic (it reads the change tracker, not SQL), so it protects every runtime context the platform builds —
/// the API host and every worker job — wired once in <c>UseLiveCoreNpgsql</c>.
///
/// DEFENCE IN DEPTH, NOT THE ONLY LAYER. This is the in-process prevention; it is paired with the non-tracked
/// audit reads (so a row is not normally tracked in the first place), the DB-level <c>REVOKE UPDATE, DELETE ON
/// audit_logs</c> migration (so the runtime database role cannot do it even out of band) and the hash chain (so
/// any tampering that bypasses all of the above is still DETECTED). A raw-SQL UPDATE/DELETE does not go through
/// <c>SaveChanges</c>, so it is intentionally NOT caught here — that is the REVOKE's and the chain's job.
///
/// CONTENT-FREE (threat T7). It reads only the change-tracker state and the entries' ids; it never reads or logs
/// any recorded field, and the thrown exception carries ids and a state name only.
/// </summary>
internal sealed class AuditLogTamperProtectionInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        GuardAgainstAuditLogMutation(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        GuardAgainstAuditLogMutation(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Fails closed if the change tracker holds any <see cref="AuditLogEntry"/> marked
    /// <see cref="EntityState.Modified"/> or <see cref="EntityState.Deleted"/> — the two states that would emit an
    /// UPDATE or DELETE against <c>audit_logs</c>. An append (<see cref="EntityState.Added"/>) and an unchanged
    /// entry are left alone. The change detector has already run by the time the SavingChanges interceptor fires,
    /// so the entries' states are current.
    /// </summary>
    private static void GuardAgainstAuditLogMutation(DbContext? context)
    {
        // A null context (a no-op SaveChanges with no associated context) has nothing to guard.
        if (context is null)
        {
            return;
        }

        List<Guid>? modifiedIds = null;
        List<Guid>? deletedIds = null;

        foreach (var entry in context.ChangeTracker.Entries<AuditLogEntry>())
        {
            switch (entry.State)
            {
                case EntityState.Modified:
                    (modifiedIds ??= []).Add(entry.Entity.Id);
                    break;
                case EntityState.Deleted:
                    (deletedIds ??= []).Add(entry.Entity.Id);
                    break;
                default:
                    // Added (the append path) and Unchanged/Detached are not mutations: leave them alone.
                    break;
            }
        }

        // Report the first rejected kind found (deletion is reported on its own so the exception names the exact
        // operation); either kind aborts the whole SaveChanges fail-closed before it reaches the database.
        if (deletedIds is not null)
        {
            throw new AuditLogTamperException(deletedIds, nameof(EntityState.Deleted));
        }

        if (modifiedIds is not null)
        {
            throw new AuditLogTamperException(modifiedIds, nameof(EntityState.Modified));
        }
    }
}
