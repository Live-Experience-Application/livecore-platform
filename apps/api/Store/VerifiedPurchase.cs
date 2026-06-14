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
/// <see cref="ProductReference"/> — plus the <see cref="Environment"/> the proof was verified in. It carries no
/// proof, no receipt body and no entitlement decision: turning a verified purchase into granted entitlements is
/// the entitlement model's job (the "Entitlements and Quotas" epic), kept separate so a verified purchase is never
/// confused with the premium state it later produces.
///
/// SANDBOX vs PRODUCTION (CORE-MON-008). <see cref="Environment"/> is how a conforming adapter REPORTS whether it
/// verified the proof against the provider's sandbox/test environment or its live production environment, so Core
/// can keep the two apart through the fail-closed <see cref="PurchaseEnvironmentPolicy"/> ("a fail-closed port that
/// distinguishes sandbox vs production"). The adapter MUST set it from the verified receipt; when it genuinely
/// cannot determine the environment it reports the fail-closed default (<see cref="PurchaseEnvironment.Sandbox"/>,
/// which a production deployment refuses to honor), so an under-reporting adapter never silently unlocks premium
/// in production.
///
/// These are stable identifiers, not secrets, so they are safe in structured logs (threat T7 concerns content,
/// not identifiers; docs/07_SECURITY_THREAT_MODEL.md) — unlike the raw proof, which <see cref="PurchaseVerificationRequest"/>
/// keeps out of its <c>ToString</c>.
/// </summary>
public sealed class VerifiedPurchase
{
    /// <summary>The maximum length of a <see cref="ProviderTransactionId"/> or a <see cref="ProductReference"/>.</summary>
    public const int MaxIdentifierLength = 256;

    private VerifiedPurchase(
        PurchaseProvider provider,
        string providerTransactionId,
        string productReference,
        PurchaseEnvironment environment)
    {
        Provider = provider;
        ProviderTransactionId = providerTransactionId;
        ProductReference = productReference;
        Environment = environment;
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
    /// The provider environment this purchase's proof was verified in — the provider's sandbox/test environment
    /// or its live production environment (CORE-MON-008). Core keeps the two apart through the fail-closed
    /// <see cref="PurchaseEnvironmentPolicy"/>: a production deployment honors only a
    /// <see cref="PurchaseEnvironment.Production"/> purchase. The adapter reports it from the verified receipt.
    /// </summary>
    public PurchaseEnvironment Environment { get; }

    /// <summary>
    /// Creates a normalized verified purchase from the identifiers a provider adapter extracted after a
    /// successful verification. All three identifiers are required: a verified purchase always names its
    /// provider, its provider transaction and its product. The two string identifiers are trimmed and must be
    /// non-blank and within <see cref="MaxIdentifierLength"/>.
    ///
    /// A conforming adapter MUST report the <paramref name="environment"/> the proof was verified in
    /// (CORE-MON-008). It defaults to the fail-closed <see cref="PurchaseEnvironment.Sandbox"/> — the value a
    /// production deployment refuses to honor — so an adapter that omits it can never silently unlock premium in
    /// production; a real adapter that knows the environment passes it explicitly.
    /// </summary>
    /// <param name="provider">The store that verified the purchase.</param>
    /// <param name="providerTransactionId">The provider-assigned transaction identifier.</param>
    /// <param name="productReference">The purchased product/subscription identifier.</param>
    /// <param name="environment">The provider environment the proof was verified in (fail-closed default: sandbox).</param>
    /// <exception cref="ArgumentOutOfRangeException">The provider or environment is not a defined value.</exception>
    /// <exception cref="ArgumentException">A required identifier is blank or too long.</exception>
    public static VerifiedPurchase Create(
        PurchaseProvider provider,
        string providerTransactionId,
        string productReference,
        PurchaseEnvironment environment = PurchaseEnvironment.Sandbox)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (!Enum.IsDefined(environment))
        {
            throw new ArgumentOutOfRangeException(nameof(environment), environment, "Environment is not a defined purchase environment.");
        }

        var normalizedTransactionId = NormalizeIdentifier(providerTransactionId, nameof(providerTransactionId));
        var normalizedProductReference = NormalizeIdentifier(productReference, nameof(productReference));

        return new VerifiedPurchase(provider, normalizedTransactionId, normalizedProductReference, environment);
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
    /// Identifier-only representation, safe for structured logs: the provider, transaction id, product reference
    /// and verified environment are stable identifiers, never receipt content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"VerifiedPurchase provider={Provider} transactionId={ProviderTransactionId} productReference={ProductReference} environment={Environment}";
}
