// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Audit;

/// <summary>
/// The INCREMENTAL state machine behind audit-log hash-chain verification (CORE-SEC-003): it accepts a tenant's
/// entries ONE AT A TIME in append (<see cref="AuditLogEntry.Sequence"/>) order and accumulates the running
/// verification outcome, so a chain can be verified as it is STREAMED in bounded segments rather than materialized
/// whole (CORE-PERF-005). It holds only the running counts, the single PRECEDING chained entry and the first
/// break — O(1) state, never the whole chain — so the verifier's memory stays bounded as the audit log grows.
///
/// It performs the exact three per-entry checks <see cref="AuditLogChain.Verify"/> documents — content integrity,
/// linkage and contiguity — and reports the FIRST break, so feeding the chain one segment at a time produces the
/// identical outcome as verifying a fully materialized list: detection of a tampered, deleted, inserted or
/// reordered chain is unchanged. <see cref="AuditLogChain.Verify"/> is itself implemented on top of this type over
/// an in-memory list, so the verification logic lives in ONE place (the in-memory and the streamed reads cannot
/// drift apart). It is content-free (identifiers and counts only, threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal sealed class AuditLogChainVerification
{
    private int _legacyCount;
    private int _verifiedCount;
    private AuditLogEntry? _previousChainedEntry;
    private AuditLogChainVerificationResult? _firstBreak;

    /// <summary>
    /// Whether a break has already been found. Once <see langword="true"/>, no further entry can change the
    /// outcome (the result pinpoints the FIRST broken entry), so the streaming verifier stops reading segments.
    /// </summary>
    public bool IsBroken => _firstBreak is not null;

    /// <summary>
    /// Verifies the next entry in append (sequence) order against the running state. Returns <see langword="true"/>
    /// while the chain is still intact and the caller should keep feeding entries, or <see langword="false"/> once
    /// a break is found — the streaming verifier stops reading further segments then, because later entries cannot
    /// change the verdict. Calling it after a break is a safe no-op that keeps returning <see langword="false"/>.
    /// </summary>
    /// <param name="entry">The next entry in append order (required).</param>
    public bool Accept(AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (_firstBreak is not null)
        {
            return false;
        }

        // Entries written before the chain existed carry no hash; they are not part of the verifiable chain
        // (their integrity predates this control) and are counted, not checked — the first hashed entry after a
        // legacy run starts a fresh genesis. Identical to AuditLogChain.Verify's legacy handling.
        if (entry.EntryHash is null)
        {
            _legacyCount++;
            return true;
        }

        var expectedPreviousHash = _previousChainedEntry?.EntryHash;

        // LINKAGE: the stored previous hash must point at the prior chained entry (null for the genesis).
        if (!string.Equals(entry.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
        {
            _firstBreak = AuditLogChainVerificationResult.Broken(
                _verifiedCount,
                _legacyCount,
                entry,
                "The entry's previous-hash link does not match the preceding entry (an entry was inserted, removed or reordered).");
            return false;
        }

        // CONTIGUITY: a deleted entry leaves a gap in the gap-free per-tenant sequence.
        if (_previousChainedEntry is not null && entry.Sequence != _previousChainedEntry.Sequence + 1)
        {
            _firstBreak = AuditLogChainVerificationResult.Broken(
                _verifiedCount,
                _legacyCount,
                entry,
                "The entry's sequence is not contiguous with the preceding entry (an entry was deleted).");
            return false;
        }

        // CONTENT INTEGRITY: recompute the hash over the recorded fields and the stored previous hash.
        var recomputed = AuditLogChain.ComputeEntryHash(entry, entry.PreviousHash);
        if (!string.Equals(recomputed, entry.EntryHash, StringComparison.Ordinal))
        {
            _firstBreak = AuditLogChainVerificationResult.Broken(
                _verifiedCount,
                _legacyCount,
                entry,
                "The entry's stored hash does not match its recorded content (the entry was altered).");
            return false;
        }

        _verifiedCount++;
        _previousChainedEntry = entry;
        return true;
    }

    /// <summary>
    /// The accumulated result so far: the first break if one was found, otherwise a valid result with the verified
    /// and legacy counts. Safe to call at any point; the streaming verifier calls it once the chain is fully read
    /// or a break is found.
    /// </summary>
    public AuditLogChainVerificationResult ToResult()
        => _firstBreak ?? AuditLogChainVerificationResult.Valid(_verifiedCount, _legacyCount);
}
