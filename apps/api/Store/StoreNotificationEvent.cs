namespace LiveCore.Api.Store;

/// <summary>
/// The persisted record of one handled store server notification (CORE-STORE-005, the idempotent store
/// notification handling story of the "Store Notifications" epic). A <see cref="StoreNotificationEvent"/> is the
/// Store module's <c>store_notification_events</c> row (one of the module's documented "Database additions",
/// docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md; csv/database_tables.csv). It plays two roles at once:
///
/// THE IDEMPOTENCY LEDGER. A purchase store delivers a notification with its own unique notification id (Apple's
/// <c>notificationUUID</c>, Google's Pub/Sub <c>messageId</c>), and a store may re-deliver the SAME notification
/// (at-least-once delivery, client/network retries). The pair (<see cref="Provider"/>,
/// <see cref="ProviderNotificationId"/>) idempotently names one delivered notification, and the unique
/// <c>store_notification_events(provider, provider_notification_id)</c> index makes handling it twice a safe
/// no-op that records no second row and applies no second effect — "Store notifications must be idempotent"
/// (docs/21 "Security requirements"). This is the same idempotency shape as the unique
/// <c>purchase_transactions(provider, provider_transaction_id)</c> and <c>idempotency_keys(scope, key)</c> indexes.
///
/// THE AUDIT FACT. Each row records what the notification was and what it did — the
/// <see cref="NotificationType"/>, the <see cref="ProviderTransactionId"/> of the affected purchase, the
/// <see cref="AppliedStatus"/> the notification drove the purchase to, and the processing
/// <see cref="Outcome"/> — so the arrival and effect of every store notification is an immutable, auditable fact
/// (the event catalog's <c>StoreNotificationProcessed</c>; csv/entitlement_event_catalog.csv). It is APPEND-ONLY
/// (mirrors <c>audit_logs</c>, <c>session_events</c> and <c>purchase_events</c>): there is no update or delete
/// path. The purchase lifecycle change it caused is separately audited by the append-only <c>purchase_events</c>
/// trail (CORE-STORE-002), so this row is the notification-side fact and the purchase event is the purchase-side
/// fact.
///
/// NORMALIZED, NO RAW PAYLOAD, NO SECRETS. The row stores only the NORMALIZED identifiers and enums the
/// deployment-supplied parser extracted after validating the payload — never the raw notification body, which may
/// embed signed receipt content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md), exactly as the raw verification
/// proof is never persisted. The stored identifiers (the provider notification id, the provider transaction id)
/// are stable identifiers, not secrets, so <see cref="ToString"/> may carry them for structured logs.
/// </summary>
public sealed class StoreNotificationEvent
{
    /// <summary>
    /// Maximum stored length of the <see cref="ProviderNotificationId"/> and <see cref="ProviderTransactionId"/>
    /// identifiers — the same bound a <see cref="StoreNotification"/> normalizes them to, so a normalized
    /// notification always fits.
    /// </summary>
    public const int MaxIdentifierLength = StoreNotification.MaxIdentifierLength;

    private StoreNotificationEvent(
        Guid id,
        PurchaseProvider provider,
        string providerNotificationId,
        StoreNotificationType notificationType,
        string providerTransactionId,
        PurchaseTransactionStatus appliedStatus,
        StoreNotificationProcessingOutcome outcome,
        DateTimeOffset receivedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Store notification event id must not be empty.", nameof(id));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Provider is not a defined purchase provider.");
        }

        if (!Enum.IsDefined(notificationType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(notificationType),
                notificationType,
                "Notification type is not a defined store notification type.");
        }

        if (!Enum.IsDefined(appliedStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedStatus),
                appliedStatus,
                "Applied status is not a defined purchase transaction status.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Outcome is not a defined store notification processing outcome.");
        }

        // The AlreadyProcessed outcome is the pre-persistence dedup short-circuit and is never recorded: a
        // duplicate writes no second ledger row, so persisting it would be a contradiction.
        if (outcome == StoreNotificationProcessingOutcome.AlreadyProcessed)
        {
            throw new ArgumentException(
                "A recorded store notification event must carry a first-processing outcome, not AlreadyProcessed.",
                nameof(outcome));
        }

        ProviderNotificationId = NormalizeIdentifier(providerNotificationId, nameof(providerNotificationId));
        ProviderTransactionId = NormalizeIdentifier(providerTransactionId, nameof(providerTransactionId));

        Id = id;
        Provider = provider;
        NotificationType = notificationType;
        AppliedStatus = appliedStatus;
        Outcome = outcome;
        // Normalized to UTC so the persisted timestamptz value is offset-independent (docs/10_DATABASE_SCHEMA.md).
        ReceivedAt = receivedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private StoreNotificationEvent()
    {
        // Non-null reference defaults for the EF materialization path; the real values are set from the stored
        // columns.
        ProviderNotificationId = null!;
        ProviderTransactionId = null!;
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
    public Guid Id { get; }

    /// <summary>The store that delivered the notification. Part of the idempotency key.</summary>
    public PurchaseProvider Provider { get; }

    /// <summary>
    /// The store's own unique notification identifier (Apple's <c>notificationUUID</c>, Google's Pub/Sub
    /// <c>messageId</c>). Unique within its <see cref="Provider"/>, so the pair (<see cref="Provider"/>,
    /// <see cref="ProviderNotificationId"/>) idempotently names one delivered notification (the dedup key).
    /// </summary>
    public string ProviderNotificationId { get; }

    /// <summary>The actionable kind of the handled notification (a renewal, cancellation, refund or grace period).</summary>
    public StoreNotificationType NotificationType { get; }

    /// <summary>The provider transaction id of the purchase the notification concerned.</summary>
    public string ProviderTransactionId { get; }

    /// <summary>The purchase lifecycle status the notification drove the purchase to (the normalized effect).</summary>
    public PurchaseTransactionStatus AppliedStatus { get; }

    /// <summary>What handling the notification did to the purchase (applied / unchanged / no purchase found).</summary>
    public StoreNotificationProcessingOutcome Outcome { get; }

    /// <summary>When the notification was received and handled (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>
    /// Records a handled store notification as a new ledger/audit row. Built from the normalized
    /// <see cref="StoreNotification"/> the parser produced plus the processing <paramref name="outcome"/> the
    /// handler determined; the applied status is the one the notification's <see cref="StoreNotification.Type"/>
    /// implies, so the row is self-describing.
    /// </summary>
    /// <param name="notification">The normalized notification that was handled.</param>
    /// <param name="outcome">The first-processing outcome (never <see cref="StoreNotificationProcessingOutcome.AlreadyProcessed"/>).</param>
    /// <param name="receivedAt">When the notification was received and handled.</param>
    /// <exception cref="ArgumentNullException"><paramref name="notification"/> is null.</exception>
    /// <exception cref="ArgumentException">The outcome is <see cref="StoreNotificationProcessingOutcome.AlreadyProcessed"/>.</exception>
    public static StoreNotificationEvent Record(
        StoreNotification notification,
        StoreNotificationProcessingOutcome outcome,
        DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return new StoreNotificationEvent(
            Guid.CreateVersion7(),
            notification.Provider,
            notification.ProviderNotificationId,
            notification.Type,
            notification.ProviderTransactionId,
            notification.Type.ToTransactionStatus(),
            outcome,
            receivedAt);
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
    /// Identifier-only representation that is safe for structured logs: the row id, provider, notification id,
    /// type, affected transaction id, applied status and outcome. These are stable identifiers and enums, never
    /// the raw payload or any receipt content (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"StoreNotificationEvent {Id} provider={Provider} notificationId={ProviderNotificationId} "
            + $"type={NotificationType} transactionId={ProviderTransactionId} appliedStatus={AppliedStatus} outcome={Outcome}";
}
