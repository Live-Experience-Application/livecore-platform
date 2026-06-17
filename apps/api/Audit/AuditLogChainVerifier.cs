// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// The operational VERIFICATION ROUTINE for a tenant's tamper-evident audit-log hash chain (CORE-SEC-003): it
/// reads a tenant's entries in append order through the append-only persistence contract and runs
/// <see cref="AuditLogChain.Verify"/> over them, returning an <see cref="AuditLogChainVerificationResult"/>. It
/// is the read-side counterpart to the chain that <see cref="AuditLogRepository.AppendAsync"/> builds on the
/// write side, and it is the unit a deployment runs (from an admin/operator tool, a scheduled integrity check
/// or a future endpoint) to answer "has this tenant's audit history been altered?".
///
/// TENANT-SCOPED (threat T5). The chain is read ONLY through
/// <see cref="IAuditLogRepository.ListChainByOrganizationAsync"/> for the requested organization, which filters
/// by <c>organization_id</c>, so verifying one tenant never reads — and a break in one tenant never implicates —
/// another tenant's records, exactly as the audit read endpoint (CORE-SEC-002) is tenant-scoped. There is no
/// list-everything verification: a caller verifies one named tenant at a time.
///
/// This routine performs NO authorization itself — it is the reusable verification core, mirroring how
/// <see cref="AuditQueryPolicy"/> is the reusable read authorization and the projection. A future HTTP surface
/// that exposes verification would compose the trusted tenant resolution and the "View audit log" grant on top
/// of this, exactly as <see cref="AuditLogEndpoints"/> composes them over the read. The result carries only
/// identifiers and counts, never recorded content (threat T7).
/// </summary>
internal sealed class AuditLogChainVerifier
{
    private readonly IAuditLogRepository _auditLog;

    public AuditLogChainVerifier(IAuditLogRepository auditLog)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        _auditLog = auditLog;
    }

    /// <summary>
    /// Verifies the hash chain of the given tenant's append-only audit log. Reads the tenant's entries in append
    /// (per-tenant sequence) order and returns the <see cref="AuditLogChain.Verify"/> outcome: valid when the
    /// chain is intact, or the first break (entry id, sequence and a generic reason) when a persisted entry has
    /// been altered, deleted, inserted or reordered.
    /// </summary>
    /// <param name="organizationId">The tenant whose chain is verified (required, non-empty).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    public async Task<AuditLogChainVerificationResult> VerifyAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        var entries = await _auditLog
            .ListChainByOrganizationAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);

        return AuditLogChain.Verify(entries);
    }
}
