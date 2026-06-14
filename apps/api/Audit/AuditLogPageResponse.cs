namespace LiveCore.Api.Audit;

/// <summary>
/// Safe, generic response of the view-audit-log read endpoint (CORE-SEC-002, the "Security Hardening" epic;
/// <c>GET /api/v1/audit-logs</c>) — one tenant-scoped, paged page of the append-only audit log handed to an
/// authorized reader (Owner/Admin/Auditor, the "View audit log" permission of
/// docs/06_AUTHORIZATION_MATRIX.md). It is the HTTP shape the endpoint builds from the already-authorized,
/// already-projected <see cref="AuditLogEntryView"/> list, so the read path is the documented composition of
/// the existing parts: resolve the trusted tenant, gate on <see cref="AuditQueryPolicy.CanViewAuditLog"/>,
/// read the tenant-scoped page, and <see cref="AuditQueryPolicy.Project"/> it (the AuditQueryPolicy xmldoc:
/// "A future audit query endpoint composes the two layers").
///
/// TENANT-SCOPED. <see cref="OrganizationId"/> echoes the single tenant the page belongs to (the
/// <c>organization_id</c> the entries were read under); the endpoint only ever reads the resolved tenant's
/// log, so a page can never carry another tenant's records (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// PAGED. The audit log grows without bound, so it is never returned whole: <see cref="Entries"/> is at most
/// <see cref="Limit"/> entries starting at <see cref="Offset"/>, in chronological (append) order, and
/// <see cref="HasMore"/> tells the client whether a further page exists (request the next page at
/// <c>offset = Offset + Entries.Count</c>). The page bounds are server-enforced: a request for more than
/// <see cref="MaxLimit"/> is clamped to it.
///
/// NO PII / NO SECRETS (threat T7). Every entry is the <see cref="AuditLogEntryView"/> projection — only
/// identifiers, enums and generic state names, never a display name, an email, an access token, a storage
/// coordinate or any resolved scene/content body — and no internal authorization rationale is ever echoed
/// (docs/08_API_CONTRACTS.md). The audit log records the FACT of a security-relevant action, so this view
/// carries the fact and nothing more.
/// </summary>
/// <param name="OrganizationId">The single tenant this page of audit entries belongs to.</param>
/// <param name="Offset">The zero-based index of the first entry in this page within the tenant's audit stream.</param>
/// <param name="Limit">The effective (server-clamped) maximum number of entries this page may contain.</param>
/// <param name="HasMore">Whether at least one further entry exists after this page (request it at the next offset).</param>
/// <param name="Entries">The tenant-scoped, PII/secret-free audit entries, in chronological (append) order.</param>
public sealed record AuditLogPageResponse(
    Guid OrganizationId,
    int Offset,
    int Limit,
    bool HasMore,
    IReadOnlyList<AuditLogEntryView> Entries)
{
    /// <summary>Default page size used when the request supplies no <c>limit</c>.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Hard server-side ceiling on the page size. A request for a larger <c>limit</c> is clamped to this so a
    /// single read can never materialize an unbounded number of audit rows (a DoS/amplification guard layered
    /// under the per-principal rate limit, CORE-SEC-001).
    /// </summary>
    public const int MaxLimit = 200;
}
