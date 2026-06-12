namespace LiveCore.Api.Store;

/// <summary>
/// The provider-neutral INPUT to store notification handling (CORE-STORE-005) — one raw, opaque server
/// notification a store delivered to a notification endpoint, addressed to one <see cref="PurchaseProvider"/>,
/// for the deployment-supplied <see cref="IStoreNotificationParser"/> to validate and normalize.
///
/// THE ABSTRACTION'S SEAM. This is the one shape Core builds from an inbound HTTP callback and hands to the
/// parser port; the payload's INTERNAL structure (an Apple App Store Server Notification V2 <c>signedPayload</c>
/// JWS, a Google RTDN Pub/Sub push body) is opaque to Core and is parsed only inside the provider-specific
/// adapter — so provider logic stays isolated from Core domain logic (the verification abstraction's seam,
/// CORE-STORE-001, applied to notifications).
///
/// THE PAYLOAD IS UNTRUSTED AND IS NOT TRUSTED OR LOGGED. A store notification endpoint is unauthenticated at the
/// HTTP layer (csv/mobile_store_api_routes.csv: the two store-notification routes are <c>auth_required=false</c>),
/// so anyone can POST to it; the payload is authentic ONLY once the adapter validates its signature/source
/// ("Must validate signature/idempotency" / "Must validate source/idempotency", csv/mobile_store_api_routes.csv).
/// Until then it is an untrusted blob that could be forged or replayed, and it may embed signed receipt content,
/// so <see cref="ToString"/> EXCLUDES <see cref="RawPayload"/> and reveals only the provider and the payload
/// length (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), exactly as <see cref="PurchaseVerificationRequest.ToString"/>
/// excludes the proof.
/// </summary>
public sealed class StoreNotificationEnvelope
{
    /// <summary>
    /// The maximum accepted length of a <see cref="RawPayload"/>. A store notification (an Apple signed payload
    /// or a Google RTDN push body) is at most a few kilobytes; this generous ceiling accepts any legitimate
    /// notification while rejecting an abusive, oversized body before it reaches a parser adapter (the
    /// notification analogue of <see cref="PurchaseVerificationRequest.MaxProofLength"/>, with headroom for a
    /// base64 Pub/Sub envelope).
    /// </summary>
    public const int MaxRawPayloadLength = 65536;

    private StoreNotificationEnvelope(PurchaseProvider provider, string rawPayload)
    {
        Provider = provider;
        RawPayload = rawPayload;
    }

    /// <summary>The store the notification was delivered from, so Core can select the right parser.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The opaque raw notification payload the store delivered. Opaque to Core: it is carried verbatim to the
    /// provider adapter and never parsed, trusted or logged here. An UNTRUSTED blob until the adapter validates
    /// its signature/source — see the type remarks and <see cref="ToString"/>.
    /// </summary>
    public string RawPayload { get; }

    /// <summary>
    /// Creates a provider-neutral notification envelope. The payload must be present (non-blank) and within
    /// <see cref="MaxRawPayloadLength"/>; it is stored VERBATIM (provider notification material is never mutated
    /// before the adapter validates it).
    /// </summary>
    /// <param name="provider">The store the notification was delivered from.</param>
    /// <param name="rawPayload">The opaque raw notification payload.</param>
    /// <exception cref="ArgumentOutOfRangeException">The provider is not a defined value.</exception>
    /// <exception cref="ArgumentException">The payload is blank or too long.</exception>
    public static StoreNotificationEnvelope Create(PurchaseProvider provider, string rawPayload)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            throw new ArgumentException("A store notification payload must be provided.", nameof(rawPayload));
        }

        if (rawPayload.Length > MaxRawPayloadLength)
        {
            throw new ArgumentException(
                $"A store notification payload must be at most {MaxRawPayloadLength} characters.",
                nameof(rawPayload));
        }

        return new StoreNotificationEnvelope(provider, rawPayload);
    }

    /// <summary>
    /// Log-safe representation: the provider and the payload length only. The <see cref="RawPayload"/> is an
    /// unvalidated, possibly forged or receipt-bearing blob and is deliberately EXCLUDED so a log line can never
    /// leak or replay it (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"StoreNotificationEnvelope provider={Provider} payloadLength={RawPayload.Length}";
}
