namespace LiveCore.Api.Store;

/// <summary>
/// The provider-neutral INPUT to purchase verification (CORE-STORE-001) — the opaque proof a mobile client
/// submitted, addressed to one <see cref="PurchaseProvider"/>, for the backend to verify before it grants
/// any entitlement (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification": "Mobile app
/// sends transaction token/JWS/purchase token to backend; Backend verifies with Apple/Google server APIs").
///
/// THE ABSTRACTION'S SEAM. This is the one shape Core domain logic builds and hands to the
/// <see cref="IPurchaseVerificationProvider"/> port; the proof's INTERNAL structure (an Apple signed-transaction
/// JWS, a Google purchase token) is opaque to Core and is parsed only inside the deployment-supplied,
/// provider-specific adapter — so "Apple/Google provider logic is isolated from Core domain logic" (the epic
/// acceptance criterion). Core never inspects, decodes or trusts the proof; it only carries it to the verifier.
///
/// THE PROOF IS A SECRET. The proof is verification material that, until verified, must not be trusted and
/// must never be logged (a client could submit a forged or replayed token; "Never trust client-side premium
/// flags", docs/21). <see cref="ToString"/> therefore EXCLUDES <see cref="Proof"/> and reveals only the
/// provider and whether a product reference is present (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), exactly
/// as <c>SignedAssetUrl.ToString</c> excludes the signed URL.
///
/// This request carries NO subject identity and NO tenant: WHO is verifying and WHETHER they are authorized is
/// the verification endpoint's server-side responsibility (the later CORE-STORE-003/004), not this neutral
/// value's. The request is constructed only after that authorization passes.
/// </summary>
public sealed class PurchaseVerificationRequest
{
    /// <summary>
    /// The maximum length of a <see cref="Proof"/>. A store proof (an Apple signed-transaction JWS or a Google
    /// purchase token) is at most a few kilobytes; this generous ceiling accepts any legitimate proof while
    /// rejecting an abusive, oversized payload before it reaches a provider adapter.
    /// </summary>
    public const int MaxProofLength = 8192;

    /// <summary>The maximum length of a <see cref="ProductReference"/> (a store product/subscription identifier).</summary>
    public const int MaxProductReferenceLength = 256;

    private PurchaseVerificationRequest(PurchaseProvider provider, string proof, string? productReference)
    {
        Provider = provider;
        Proof = proof;
        ProductReference = productReference;
    }

    /// <summary>The store the proof must be verified against, so Core can select the right verifier.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The opaque proof of purchase the client submitted (an Apple signed-transaction JWS or a Google purchase
    /// token). Opaque to Core: it is carried verbatim to the provider adapter and never parsed, trusted or
    /// logged here. A SECRET — see the type remarks and <see cref="ToString"/>.
    /// </summary>
    public string Proof { get; }

    /// <summary>
    /// An optional, opaque store product/subscription identifier the client submitted alongside the proof (some
    /// providers address verification by product as well as token). Opaque to Core — never interpreted here.
    /// <see langword="null"/> when the client supplied none.
    /// </summary>
    public string? ProductReference { get; }

    /// <summary>
    /// Creates a provider-neutral verification request. The proof must be present (non-blank) and within
    /// <see cref="MaxProofLength"/>; it is stored VERBATIM (provider verification material is never mutated).
    /// The optional product reference, when supplied, is trimmed and must be non-blank and within
    /// <see cref="MaxProductReferenceLength"/>.
    /// </summary>
    /// <param name="provider">The store the proof is verified against.</param>
    /// <param name="proof">The opaque proof of purchase (transaction token / JWS / purchase token).</param>
    /// <param name="productReference">An optional opaque store product/subscription identifier, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not a defined value.</exception>
    /// <exception cref="ArgumentException">The proof is blank or too long, or a supplied product reference is blank or too long.</exception>
    public static PurchaseVerificationRequest Create(
        PurchaseProvider provider,
        string proof,
        string? productReference = null)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (string.IsNullOrWhiteSpace(proof))
        {
            throw new ArgumentException("A purchase verification proof must be provided.", nameof(proof));
        }

        if (proof.Length > MaxProofLength)
        {
            throw new ArgumentException(
                $"A purchase verification proof must be at most {MaxProofLength} characters.",
                nameof(proof));
        }

        string? normalizedProductReference = null;
        if (productReference is not null)
        {
            normalizedProductReference = productReference.Trim();
            if (normalizedProductReference.Length == 0)
            {
                throw new ArgumentException("A product reference, when supplied, must not be blank.", nameof(productReference));
            }

            if (normalizedProductReference.Length > MaxProductReferenceLength)
            {
                throw new ArgumentException(
                    $"A product reference must be at most {MaxProductReferenceLength} characters.",
                    nameof(productReference));
            }
        }

        return new PurchaseVerificationRequest(provider, proof, normalizedProductReference);
    }

    /// <summary>
    /// Log-safe representation: the provider and whether a product reference is present only. The
    /// <see cref="Proof"/> is a secret (an unverified, possibly forged credential-like token) and is deliberately
    /// EXCLUDED so a log line can never leak or replay it (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"PurchaseVerificationRequest provider={Provider} hasProductReference={ProductReference is not null}";
}
