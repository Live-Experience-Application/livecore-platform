// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// Persistence contract for the append-only audit log (CORE-VIS-006). The Audit module owns the
/// <c>audit_logs</c> table; other modules write security events only through this contract
/// (docs/05_MODULE_CONTRACTS.md: the Audit module owns the "append-only audit log" and "security event
/// records"; docs/02_ARCHITECTURE.md: modules never query foreign tables directly).
///
/// APPEND-ONLY. The contract intentionally exposes only an append and a tenant-scoped read: there is
/// NO update and NO delete, because audit facts are immutable once written (docs/10_DATABASE_SCHEMA.md:
/// "audit logs are append-only"). The read is scoped by organization id — the documented critical index
/// <c>audit_logs(organization_id, created_at)</c> — so one tenant's audit records are never returned
/// through another tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). There is no
/// list-everything method and no by-id-alone lookup.
///
/// The per-action AUTHORIZATION of who may read the audit log ("View audit log" in
/// docs/06_AUTHORIZATION_MATRIX.md — Owner/Admin/Auditor) is the <see cref="AuditQueryPolicy"/>
/// (CORE-AUD-005); this contract is the raw tenant-scoped persistence that policy sits on top of. A future
/// audit query endpoint composes the trusted tenant resolution, the policy and its projection over this
/// read (csv/api_routes.csv defines no audit route yet, so none exists).
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Appends an audit entry. The entry is immutable; this is the only write path. The surrogate id is
    /// generated non-empty by the aggregate, so an insert never collides; a foreign-key violation (a
    /// non-existent tenant) surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every audit entry of the given organization, in deterministic order — by the time-ordered
    /// surrogate id (UUIDv7), which is chronological and provider-independent. The list is tenant-scoped:
    /// the predicate matches <c>organization_id</c> (the leading column of the documented critical index
    /// <c>audit_logs(organization_id, created_at)</c>), so a foreign tenant's records are NEVER returned
    /// even when their ids would otherwise be addressable (threat T5). This is NOT a list-everything
    /// method.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    Task<IReadOnlyList<AuditLogEntry>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists a single PAGE of the given organization's audit entries, for the tenant-scoped view-audit-log
    /// read endpoint (CORE-SEC-002, <c>GET /api/v1/audit-logs</c>). It is the bounded counterpart of
    /// <see cref="ListByOrganizationAsync"/>: the result is the same tenant-scoped, chronologically ordered
    /// stream, but at most <paramref name="take"/> entries are materialized and the first
    /// <paramref name="skip"/> are passed over, so an unbounded audit log is never loaded into memory at
    /// once. Tenant scope is identical — the predicate matches <c>organization_id</c> (the leading column of
    /// the documented critical index <c>audit_logs(organization_id, created_at)</c>), so a foreign tenant's
    /// records are NEVER returned even when their ids would otherwise be addressable (threat T5).
    ///
    /// Ordering is by the time-ordered surrogate id (UUIDv7), the same provider-independent chronological
    /// order <see cref="ListByOrganizationAsync"/> uses (SQLite cannot ORDER BY a <c>DateTimeOffset</c>),
    /// so paging is stable on an append-only log: new entries append at the end (higher ids) and never shift
    /// an already-read page. The caller decides whether a further page exists, so this reads
    /// <paramref name="take"/> entries exactly; a caller wanting a has-more signal without a second COUNT
    /// requests one extra and trims.
    /// </summary>
    /// <param name="organizationId">The tenant whose audit log page is read (required, non-empty).</param>
    /// <param name="skip">How many leading entries to pass over (the page offset; zero for the first page).</param>
    /// <param name="take">The maximum number of entries to return (the page size; at least one).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skip"/> is negative or <paramref name="take"/> is less than one.</exception>
    Task<IReadOnlyList<AuditLogEntry>> ListPageByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every audit entry of the given organization in APPEND (per-tenant <see cref="AuditLogEntry.Sequence"/>)
    /// order — the order the tamper-evident hash chain is linked along (CORE-SEC-003), for
    /// <see cref="AuditLogChainVerifier"/>. It differs from <see cref="ListByOrganizationAsync"/> ONLY in the
    /// ordering key: that read orders by the event-time-ordered surrogate id (the chronological read contract,
    /// unchanged), whereas the chain must be walked in the order entries were appended, which the gap-free
    /// sequence captures even when a producer records an action out of event-time order. Tenant scope is
    /// identical — the predicate matches <c>organization_id</c>, so a foreign tenant's records are NEVER returned
    /// (threat T5) and a break in one tenant never implicates another.
    /// </summary>
    /// <param name="organizationId">The tenant whose chain is read (required, non-empty).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    Task<IReadOnlyList<AuditLogEntry>> ListChainByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken);
}
