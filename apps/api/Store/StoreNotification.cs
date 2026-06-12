namespace LiveCore.Api.Store;

/// <summary>
/// The provider-neutral, NORMALIZED identity and meaning of a store server notification that a
/// deployment-supplied <see cref="IStoreNotificationParser"/> validated and reduced from a raw provider payload
/// (CORE-STORE-005). It is the notification analogue of <see cref="VerifiedPurchase"/>: once the adapter has
/// validated the payload's signature/source and read it, it hands Core this common shape so Core domain logic —
/// which deduplicates the notification and drives the affected <see cref="PurchaseTransaction"/> lifecycle —
/// works against ONE neutral type and never against an Apple- or Google-specific payload.
///
/// It holds only the IDENTIFIERS and the meaning that name the notification and its effect:
/// <list type="bullet">
///   <item>the originating <see cref="Provider"/>;</item>
///   <item>the provider-assigned <see cref="ProviderNotificationId"/> — the store's own unique notification id
///   (Apple's <c>notificationUUID</c>, Google's Pub/Sub <c>messageId</c>), unique within its provider, which is
///   the IDEMPOTENCY KEY: the same delivered notification is processed only once ("Store notifications must be
///   idempotent", docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md);</item>
///   <item>the actionable <see cref="Type"/> the raw notification maps to;</item>
///   <item>the <see cref="ProviderTransactionId"/> of the purchase the notification concerns — the
///   (<see cref="Provider"/>, <see cref="ProviderTransactionId"/>) pair that names the
///   <see cref="PurchaseTransaction"/> to update (CORE-STORE-002);</item>
///   <item>the <see cref="OccurredAt"/> time the store reported for the event.</item>
/// </list>
///
/// It carries NO raw payload, NO signed receipt body and NO entitlement decision: validating the payload is the
/// adapter's job and turning the resulting purchase state into granted/revoked entitlements is the entitlement
/// model's, kept separate so a notification is never confused with the premium state it later affects. These are
/// stable identifiers, not secrets, so they are safe in structured logs (threat T7 concerns content, not
/// identifiers; docs/07_SECURITY_THREAT_MODEL.md) — unlike the raw payload, which
/// <see cref="StoreNotificationEnvelope"/> keeps out of its <c>ToString</c>.
/// </summary>
public sealed class StoreNotification
{
    /// <summary>The maximum length of a <see cref="ProviderNotificationId"/> or a <see cref="ProviderTransactionId"/>.</summary>
    public const int MaxIdentifierLength = 256;

    private StoreNotification(
        PurchaseProvider provider,
        string providerNotificationId,
        StoreNotificationType type,
        string providerTransactionId,
        DateTimeOffset occurredAt)
    {
        Provider = provider;
        ProviderNotificationId = providerNotificationId;
        Type = type;
        ProviderTransactionId = providerTransactionId;
        OccurredAt = occurredAt;
    }

    /// <summary>The store that delivered this notification.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The store's own unique notification identifier (Apple's <c>notificationUUID</c>, Google's Pub/Sub
    /// <c>messageId</c>). Unique within its <see cref="Provider"/>, so the pair (<see cref="Provider"/>,
    /// <see cref="ProviderNotificationId"/>) idempotently names one delivered notification — the dedup key the
    /// <c>store_notification_events(provider, provider_notification_id)</c> unique index is built on.
    /// </summary>
    public string ProviderNotificationId { get; }

    /// <summary>The actionable kind the raw notification maps to (a renewal, cancellation, refund or grace period).</summary>
    public StoreNotificationType Type { get; }

    /// <summary>
    /// The provider-assigned transaction identifier of the purchase this notification concerns. With
    /// <see cref="Provider"/> it names the <see cref="PurchaseTransaction"/> whose lifecycle the notification drives.
    /// </summary>
    public string ProviderTransactionId { get; }

    /// <summary>When the store reported the event occurred (UTC, normalized).</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>
    /// Creates a normalized store notification from the identifiers a parser adapter extracted after validating a
    /// raw payload. All identifiers are required: a normalized notification always names its provider, its
    /// provider notification id, its type and the affected provider transaction. The two string identifiers are
    /// trimmed and must be non-blank and within <see cref="MaxIdentifierLength"/>.
    /// </summary>
    /// <param name="provider">The store that delivered the notification.</param>
    /// <param name="providerNotificationId">The store's unique notification id (the dedup key).</param>
    /// <param name="type">The actionable notification kind.</param>
    /// <param name="providerTransactionId">The provider transaction id of the affected purchase.</param>
    /// <param name="occurredAt">When the store reported the event occurred.</param>
    /// <exception cref="ArgumentOutOfRangeException">The provider or the type is not a defined value.</exception>
    /// <exception cref="ArgumentException">A required identifier is blank or too long.</exception>
    public static StoreNotification Create(
        PurchaseProvider provider,
        string providerNotificationId,
        StoreNotificationType type,
        string providerTransactionId,
        DateTimeOffset occurredAt)
    {
        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Type is not a defined store notification type.");
        }

        var normalizedNotificationId = NormalizeIdentifier(providerNotificationId, nameof(providerNotificationId));
        var normalizedTransactionId = NormalizeIdentifier(providerTransactionId, nameof(providerTransactionId));

        return new StoreNotification(
            provider,
            normalizedNotificationId,
            type,
            normalizedTransactionId,
            occurredAt.ToUniversalTime());
    }

    private static string NormalizeIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A store notification identifier must be provided.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"A store notification identifier must be at most {MaxIdentifierLength} characters.",
                parameterName);
        }

        return normalized;
    }

    /// <summary>
    /// Identifier-only representation, safe for structured logs: the provider, the notification id, the type and
    /// the affected transaction id are stable identifiers/enums, never the raw payload or receipt content (threat
    /// T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"StoreNotification provider={Provider} notificationId={ProviderNotificationId} type={Type} "
            + $"transactionId={ProviderTransactionId}";
}
