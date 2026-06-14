namespace LiveCore.Api.Audit;

/// <summary>
/// Outcome of verifying a tenant's append-only audit-log hash chain (CORE-SEC-003). It is the return shape of
/// <see cref="AuditLogChain.Verify"/> and <see cref="AuditLogChainVerifier.VerifyAsync"/>: a tamper-evidence
/// REPORT, not content. <see cref="IsValid"/> is the headline — <see langword="true"/> when every chained entry
/// links and hashes correctly, <see langword="false"/> when a break is found. On a break,
/// <see cref="FirstBrokenEntryId"/>/<see cref="FirstBrokenSequence"/> pinpoint the first entry that failed and
/// <see cref="Reason"/> is a generic, identifier-only explanation (an entry was altered, deleted, inserted or
/// reordered).
///
/// <see cref="VerifiedCount"/> counts the entries that passed; <see cref="LegacyCount"/> counts entries that
/// predate the chain (written before this hardening landed, so they carry no hash and are not part of the
/// verifiable chain). Every field is an identifier or a count — never a display name, an access token or any
/// recorded scene/content body — so the result is safe to log or surface (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="IsValid">Whether the verified chain is intact (no break found).</param>
/// <param name="VerifiedCount">How many chained entries were checked and passed.</param>
/// <param name="LegacyCount">How many pre-chain (hash-less) entries were skipped.</param>
/// <param name="FirstBrokenEntryId">The first entry that failed verification, or <see langword="null"/> when valid.</param>
/// <param name="FirstBrokenSequence">The per-tenant sequence of the first failed entry, or <see langword="null"/> when valid.</param>
/// <param name="Reason">A generic, identifier-only description of the break, or <see langword="null"/> when valid.</param>
public sealed record AuditLogChainVerificationResult(
    bool IsValid,
    int VerifiedCount,
    int LegacyCount,
    Guid? FirstBrokenEntryId,
    long? FirstBrokenSequence,
    string? Reason)
{
    /// <summary>Builds the result for an intact chain.</summary>
    public static AuditLogChainVerificationResult Valid(int verifiedCount, int legacyCount)
        => new(true, verifiedCount, legacyCount, null, null, null);

    /// <summary>
    /// Builds the result for a broken chain, pinpointing the first failing entry and a generic reason.
    /// <paramref name="verifiedCount"/> is how many entries passed BEFORE the break.
    /// </summary>
    public static AuditLogChainVerificationResult Broken(
        int verifiedCount,
        int legacyCount,
        AuditLogEntry brokenEntry,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(brokenEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new AuditLogChainVerificationResult(
            false,
            verifiedCount,
            legacyCount,
            brokenEntry.Id,
            brokenEntry.Sequence,
            reason);
    }
}
