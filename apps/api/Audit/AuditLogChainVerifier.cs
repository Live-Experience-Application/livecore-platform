// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// The operational VERIFICATION ROUTINE for a tenant's tamper-evident audit-log hash chain (CORE-SEC-003): it
/// reads a tenant's entries in append order through the append-only persistence contract and feeds them to the
/// <see cref="AuditLogChainVerification"/> accumulator, returning an <see cref="AuditLogChainVerificationResult"/>.
/// It is the read-side counterpart to the chain that <see cref="AuditLogRepository.AppendAsync"/> builds on the
/// write side, and it is the unit a deployment runs (from an admin/operator tool, a scheduled integrity check
/// or a future endpoint) to answer "has this tenant's audit history been altered?".
///
/// STREAMED IN BOUNDED SEGMENTS (CORE-PERF-005). The chain is read in ordered segments via the
/// <c>(organization_id, sequence)</c> cursor — <see cref="IAuditLogRepository.ListChainSegmentByOrganizationAsync"/>
/// — instead of materializing a tenant's entire chain in memory, so verification memory and time stay BOUNDED as
/// the audit log grows: only one bounded segment plus the O(1) accumulator state is held at a time. Verifying the
/// streamed chain produces the IDENTICAL outcome as verifying a fully materialized list (the accumulator runs the
/// same per-entry checks <see cref="AuditLogChain.Verify"/> documents), so detection of a tampered, deleted,
/// inserted or reordered chain is unchanged — and verification stops at the FIRST broken entry, reading no further
/// segments.
///
/// TENANT-SCOPED (threat T5). Each segment is read for the requested organization only (the repository filters by
/// <c>organization_id</c>), so verifying one tenant never reads — and a break in one tenant never implicates —
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
    /// <summary>
    /// The number of entries read per chain segment (CORE-PERF-005). A small, fixed window keeps verification
    /// memory bounded regardless of how large a tenant's audit log grows; each audit entry is small (identifiers
    /// and short names), so a few hundred per segment is a few tens of kilobytes. Verifying a segment is read-only
    /// and stateless beyond the O(1) accumulator, so the window only trades read round-trips against per-segment
    /// memory.
    /// </summary>
    internal const int DefaultSegmentSize = 500;

    private readonly IAuditLogRepository _auditLog;
    private readonly int _segmentSize;

    public AuditLogChainVerifier(IAuditLogRepository auditLog)
        : this(auditLog, DefaultSegmentSize)
    {
    }

    /// <summary>
    /// Test seam (CORE-PERF-005): constructs the verifier with an explicit segment size so a test can cross
    /// segment boundaries over a small chain. Production uses the parameterless segment size
    /// (<see cref="DefaultSegmentSize"/>) via the public constructor; this overload is internal, so the DI
    /// container only ever sees the public one.
    /// </summary>
    internal AuditLogChainVerifier(IAuditLogRepository auditLog, int segmentSize)
    {
        ArgumentNullException.ThrowIfNull(auditLog);

        if (segmentSize < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(segmentSize),
                segmentSize,
                "The verification segment size must be at least one.");
        }

        _auditLog = auditLog;
        _segmentSize = segmentSize;
    }

    /// <summary>
    /// Verifies the hash chain of the given tenant's append-only audit log. Reads the tenant's entries in append
    /// (per-tenant sequence) order in BOUNDED SEGMENTS via the <c>(organization_id, sequence)</c> cursor
    /// (CORE-PERF-005) and returns the verification outcome: valid when the chain is intact, or the first break
    /// (entry id, sequence and a generic reason) when a persisted entry has been altered, deleted, inserted or
    /// reordered. Memory stays bounded — at most one segment plus O(1) accumulator state is held — and the read
    /// stops at the first broken entry.
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

        var verification = new AuditLogChainVerification();

        // Walk the chain in ordered segments via the (organization_id, sequence) cursor. The first segment reads
        // from the start of the chain (null cursor); each later segment continues strictly after the last entry
        // read, so the segments are a contiguous, non-overlapping window over the gap-free per-tenant sequence.
        long? cursor = null;
        while (true)
        {
            var segment = await _auditLog
                .ListChainSegmentByOrganizationAsync(organizationId, cursor, _segmentSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in segment)
            {
                // Accept returns false at the FIRST break. Detection pinpoints that entry and later entries cannot
                // change the verdict, so stop reading further segments — verification work stays bounded.
                if (!verification.Accept(entry))
                {
                    return verification.ToResult();
                }
            }

            // A segment shorter than the cap is the last one: the chain is fully read.
            if (segment.Count < _segmentSize)
            {
                return verification.ToResult();
            }

            // Advance the cursor past the last entry read. The per-tenant sequence is unique and strictly
            // increasing in this order, so the next segment continues with no gap and no overlap.
            cursor = segment[^1].Sequence;
        }
    }
}
