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
}
