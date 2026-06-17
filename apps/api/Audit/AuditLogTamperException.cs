// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// Thrown by <see cref="AuditLogTamperProtectionInterceptor"/> when a <c>LiveCoreDbContext</c> SaveChanges
/// would UPDATE or DELETE a persisted <c>audit_logs</c> row from inside the running process (CORE-SEC-004). The
/// append-only audit log is the security record of who did what; it is tamper-EVIDENT through the per-tenant hash
/// chain (CORE-SEC-003), and this exception is what makes it tamper-PROOF in code: any in-process mutation or
/// deletion of an audit entry FAILS CLOSED — the whole SaveChanges is aborted before it touches the database, so a
/// mutation never persists.
///
/// The audit log only ever APPENDS (an INSERT) and READS (a non-tracked SELECT); there is no legitimate
/// in-process UPDATE or DELETE of an audit row. A tracked mutation therefore signals a regression (a read path
/// that forgot <c>AsNoTracking</c>, or new code re-pointing the table) rather than a normal operation, so it is a
/// programming error surfaced loudly rather than a silent corruption of the trail. It is the in-process companion
/// to the DB-level <c>REVOKE UPDATE, DELETE ON audit_logs</c> migration (the runtime role cannot do it at the
/// database either) and the hash chain (which detects it if it ever happens out of band).
///
/// The message carries only identifiers — the row ids and the rejected state — never any recorded content
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), so it is safe for structured logs.
/// </summary>
public sealed class AuditLogTamperException : InvalidOperationException
{
    /// <summary>
    /// Creates a tamper error naming the offending audit row ids and the persistence state (Modified or
    /// Deleted) that was rejected.
    /// </summary>
    /// <param name="entryIds">The ids of the audit entries whose UPDATE/DELETE was blocked.</param>
    /// <param name="rejectedState">The rejected change-tracker state name (e.g. <c>Modified</c> / <c>Deleted</c>).</param>
    public AuditLogTamperException(IReadOnlyList<Guid> entryIds, string rejectedState)
        : base(
            $"The audit log is append-only and tamper-proof: a SaveChanges attempted to {rejectedState} "
            + $"{entryIds.Count} persisted audit_logs row(s) ({string.Join(", ", entryIds)}) and was blocked "
            + "(CORE-SEC-004). Audit entries are immutable once written; only INSERT (append) and SELECT (read) "
            + "are permitted.")
    {
        EntryIds = entryIds;
        RejectedState = rejectedState;
    }

    /// <summary>The ids of the audit entries whose UPDATE/DELETE the interceptor blocked.</summary>
    public IReadOnlyList<Guid> EntryIds { get; }

    /// <summary>The change-tracker state the interceptor rejected (Modified or Deleted).</summary>
    public string RejectedState { get; }
}
