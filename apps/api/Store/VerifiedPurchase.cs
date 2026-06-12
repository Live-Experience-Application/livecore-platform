namespace LiveCore.Api.Store;

/// <summary>
/// The provider-neutral, NORMALIZED identity of a purchase that a store's server APIs confirmed is genuine
/// (CORE-STORE-001). When a deployment-supplied <see cref="IPurchaseVerificationProvider"/> verifies a
/// <see cref="PurchaseVerificationRequest"/>'s proof, it reduces the provider's raw, provider-shaped response
/// to this common shape so that Core domain logic — which records the transaction (the later CORE-STORE-002)
/// and grants the resulting <c>SubjectEntitlement</c> — works against ONE neutral type and never against an
/// Apple- or Google-specific payload. This is the output half of "Apple/Google provider logic is isolated
/// from Core domain logic" (the epic acceptance criterion): the provider differences end here.
///
/// It holds only the IDENTIFIERS that uniquely name the verified purchase — the originating
/// <see cref="Provider"/>, the provider-assigned <see cref="ProviderTransactionId"/> (the idempotency key the
/// later persistence story keys a <c>purchase_transactions</c> row on) and the purchased
/// <see cref="ProductReference"/>. It carries no proof, no receipt body and no entitlement decision: turning a
/// verified purchase into granted entitlements is the entitlement model's job (the "Entitlements and Quotas"
/// epic), kept separate so a verified purchase is never confused with the premium state it later produces.
///
/// These are stable identifiers, not secrets, so they are safe in structured logs (threat T7 concerns content,
/// not identifiers; docs/07_SECURITY_THREAT_MODEL.md) — unlike the raw proof, which <see cref="PurchaseVerificationRequest"/>
/// keeps out of its <c>ToString</c>.
/// </summary>
public sealed class VerifiedPurchase
{
    /// <summary>The maximum length of a <see cref="ProviderTransactionId"/> or a <see cref="ProductReference"/>.</summary>
    public const int MaxIdentifierLength = 256;

    private VerifiedPurchase(PurchaseProvider provider, string providerTransactionId, string productReference)
    {
        Provider = provider;
        ProviderTransactionId = providerTransactionId;
        ProductReference = productReference;
    }

    /// <summary>The store that issued and verified this purchase.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The provider-assigned, opaque transaction identifier for the verified purchase (Apple's transaction id /
    /// Google's order or token-derived id). Unique within a provider, so the pair
    /// (<see cref="Provider"/>, <see cref="ProviderTransactionId"/>) idempotently names one purchase — the basis
    /// for the idempotent persistence the next story adds (CORE-STORE-002).
    /// </summary>
    public string ProviderTransactionId { get; }

    /// <summary>The opaque store product/subscription identifier the verified purchase is for.</summary>
    public string ProductReference { get; }

    /// <summary>
    /// Creates a normalized verified purchase from the identifiers a provider adapter extracted after a
    /// successful verification. All three identifiers are required: a verified purchase always names its
    /// provider, its provider transaction and its product. The two string identifiers are trimmed and must be
    /// non-blank and within <see cref="MaxIdentifierLength"/>.
    /// </summary>
    /// <param name="provider">The store that verified the purchase.</param>
    /// <param name="providerTransactionId">The provider-assigned transaction identifier.</param>
    /// <param name="productReference">The purchased product/subscription identifier.</param>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not a defined value.</exception>
    /// <exception cref="ArgumentException">A required identifier is blank or too long.</exception>
    public static VerifiedPurchase Create(
        PurchaseProvider provider,
        string providerTransactionId,
        string productReference)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        var normalizedTransactionId = NormalizeIdentifier(providerTransactionId, nameof(providerTransactionId));
        var normalizedProductReference = NormalizeIdentifier(productReference, nameof(productReference));

        return new VerifiedPurchase(provider, normalizedTransactionId, normalizedProductReference);
    }

    private static string NormalizeIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A verified purchase identifier must be provided.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"A verified purchase identifier must be at most {MaxIdentifierLength} characters.",
                parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Identifier-only representation, safe for structured logs: the provider, transaction id and product
    /// reference are stable identifiers, never receipt content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"VerifiedPurchase provider={Provider} transactionId={ProviderTransactionId} productReference={ProductReference}";
}
