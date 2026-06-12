namespace LiveCore.Api.Store;

/// <summary>
/// Request body for the Apple transaction verification endpoint (CORE-STORE-003,
/// <c>POST /api/v1/purchases/apple/transactions</c>, csv/api_routes.csv / csv/mobile_store_api_routes.csv
/// "Submit Apple transaction for server verification"). A mobile client completes a purchase against the
/// Apple App Store and submits its proof here for the backend to verify with Apple's server APIs BEFORE any
/// entitlement is granted (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification": the
/// client never becomes the source of truth for premium access).
///
/// THE PROOF IS UNTRUSTED AND OPAQUE. <see cref="Transaction"/> is the App Store signed transaction (a JWS) /
/// transaction proof the client submitted; Core treats it as an OPAQUE, untrusted token — it is carried
/// verbatim into a provider-neutral <see cref="PurchaseVerificationRequest"/> and verified only inside the
/// deployment-supplied Apple adapter, never parsed or trusted here ("Never trust client-side premium flags",
/// docs/21). It is a secret: a forged or replayed token is rejected by verification, and the proof is never
/// logged (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The optional <see cref="ProductReference"/> is an
/// opaque store product/subscription identifier some flows submit alongside the proof.
///
/// The body carries NO subject identity and NO premium claim: WHO is verifying is the authenticated caller
/// (resolved server-side from the bearer token), and WHETHER the purchase is genuine is decided only by the
/// provider verification, never by anything the client asserts. The body carries no vertical vocabulary
/// (docs/04_PRODUCT_BOUNDARIES.md).
/// </summary>
/// <param name="Transaction">
/// The opaque Apple App Store signed transaction (JWS) / transaction proof to verify. Required and bounded; a
/// missing, blank or oversize value is a 400.
/// </param>
/// <param name="ProductReference">
/// An optional opaque store product/subscription identifier submitted alongside the proof, or
/// <see langword="null"/>. Never interpreted by Core; bounded, and an oversize value is a 400.
/// </param>
public sealed record AppleTransactionVerificationRequest(
    string? Transaction,
    string? ProductReference)
{
    /// <summary>
    /// Log-safe representation: it reveals only whether a product reference is present. The
    /// <see cref="Transaction"/> proof is a secret (an unverified, possibly forged credential-like token) and
    /// is deliberately EXCLUDED so a log line can never leak or replay it (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md), exactly as <see cref="PurchaseVerificationRequest.ToString"/> excludes
    /// the proof.
    /// </summary>
    public override string ToString()
        => $"AppleTransactionVerificationRequest hasProductReference={ProductReference is not null}";
}

/// <summary>
/// Response body of a SUCCESSFUL purchase verification (CORE-STORE-003; reused by the later Google endpoint,
/// CORE-STORE-004). It is returned only after the provider verified the proof and the verified purchase was
/// persisted (CORE-STORE-002), so it is the server's confirmation that a genuine purchase is now the recorded
/// source of truth — the epic acceptance criterion "Apple transaction data is verified before entitlements are
/// granted" made observable: nothing is returned (and nothing is recorded) unless verification succeeded.
///
/// It carries only the normalized, provider-neutral IDENTITY of the verified purchase and its current
/// lifecycle status — the same identifiers a <see cref="VerifiedPurchase"/> / <see cref="PurchaseTransaction"/>
/// holds. These are stable identifiers, not secrets (the raw proof is the secret, and it is never persisted or
/// echoed), so the response is log-safe (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). It carries no internal
/// surrogate id, no buyer linkage and no authorization rationale (docs/08_API_CONTRACTS.md).
/// </summary>
/// <param name="Provider">The store that verified the purchase (for example <c>Apple</c>).</param>
/// <param name="ProviderTransactionId">The provider-assigned transaction identifier of the verified purchase.</param>
/// <param name="ProductReference">The opaque store product/subscription identifier the purchase is for.</param>
/// <param name="Status">
/// The current lifecycle status name of the recorded purchase — <c>Active</c> for a freshly verified purchase.
/// </param>
public sealed record PurchaseVerificationResponse(
    string Provider,
    string ProviderTransactionId,
    string ProductReference,
    string Status)
{
    /// <summary>Projects a recorded purchase transaction into the verification response DTO.</summary>
    public static PurchaseVerificationResponse From(PurchaseTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return new PurchaseVerificationResponse(
            transaction.Provider.ToString(),
            transaction.ProviderTransactionId,
            transaction.ProductReference,
            transaction.Status.ToString());
    }
}
