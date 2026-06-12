namespace LiveCore.Api.Store;

/// <summary>
/// The provider-neutral OUTCOME of verifying a purchase proof (CORE-STORE-001) — what an
/// <see cref="IPurchaseVerificationProvider"/> hands back. It is exactly one of two shapes: a
/// <see cref="PurchaseVerificationStatus.Verified"/> result carrying the normalized <see cref="VerifiedPurchase"/>,
/// or a <see cref="PurchaseVerificationStatus.Rejected"/> result carrying a generic, log-safe
/// <see cref="RejectionReason"/> and no purchase. The factory methods make the two mutually exclusive by
/// construction, so a caller can never read a <see cref="Purchase"/> off a rejected result (fail-closed:
/// "Never unlock limits before server verification succeeds", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md).
///
/// Like the request and the verified purchase, this is provider-neutral: every provider adapter reduces its own
/// raw response to this one shape, so Core domain logic branches on a single result type and never on an
/// Apple- or Google-specific payload (the epic acceptance criterion: "Apple/Google provider logic is isolated
/// from Core domain logic").
///
/// The <see cref="RejectionReason"/> is a GENERIC, client-safe phrase (e.g. "invalid or unverifiable proof") —
/// never the raw provider error, the proof, or any receipt content — so a denial can be surfaced and logged
/// without leaking sensitive material (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class PurchaseVerificationResult
{
    private PurchaseVerificationResult(
        PurchaseVerificationStatus status,
        VerifiedPurchase? purchase,
        string? rejectionReason)
    {
        Status = status;
        Purchase = purchase;
        RejectionReason = rejectionReason;
    }

    /// <summary>Whether the proof was verified or rejected.</summary>
    public PurchaseVerificationStatus Status { get; }

    /// <summary>
    /// The normalized verified purchase when <see cref="Status"/> is <see cref="PurchaseVerificationStatus.Verified"/>;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public VerifiedPurchase? Purchase { get; }

    /// <summary>
    /// A generic, client-safe and log-safe reason when <see cref="Status"/> is
    /// <see cref="PurchaseVerificationStatus.Rejected"/>; otherwise <see langword="null"/>. Never carries the raw
    /// provider error, the proof or receipt content (threat T7).
    /// </summary>
    public string? RejectionReason { get; }

    /// <summary>Whether the proof was verified (a convenience over <see cref="Status"/>).</summary>
    public bool IsVerified => Status == PurchaseVerificationStatus.Verified;

    /// <summary>
    /// A verified result carrying the normalized <see cref="VerifiedPurchase"/> the store confirmed.
    /// </summary>
    /// <param name="purchase">The normalized verified purchase.</param>
    /// <exception cref="ArgumentNullException">The purchase is null.</exception>
    public static PurchaseVerificationResult Verified(VerifiedPurchase purchase)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        return new PurchaseVerificationResult(PurchaseVerificationStatus.Verified, purchase, rejectionReason: null);
    }

    /// <summary>
    /// A rejected result carrying a generic, client-safe reason and no purchase. The proof was not a genuine,
    /// grantable purchase, so no entitlement is granted (fail-closed).
    /// </summary>
    /// <param name="reason">A generic, log-safe reason (never the raw provider error, the proof or receipt content).</param>
    /// <exception cref="ArgumentException">The reason is blank.</exception>
    public static PurchaseVerificationResult Rejected(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason must be provided.", nameof(reason));
        }

        return new PurchaseVerificationResult(PurchaseVerificationStatus.Rejected, purchase: null, reason.Trim());
    }

    /// <summary>
    /// Log-safe representation: the status, plus the verified purchase identifiers or the generic rejection
    /// reason. Both branches carry only identifiers / a generic phrase, never the proof or receipt content
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => IsVerified
            ? $"PurchaseVerificationResult status={Status} purchase=[{Purchase}]"
            : $"PurchaseVerificationResult status={Status} reason={RejectionReason}";
}
