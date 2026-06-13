namespace LiveCore.Api.Store;

/// <summary>
/// Handles validated, normalized store notifications idempotently and drives the affected purchase's lifecycle
/// (CORE-STORE-005, the idempotent store notification handling story of the "Store Notifications" epic). It is the
/// application service behind the store notification endpoints: given a <see cref="StoreNotification"/> a
/// deployment-supplied <see cref="IStoreNotificationParser"/> already validated and normalized, it updates
/// entitlement state safely — "Store server notifications update entitlement state on renewals, cancellations,
/// refunds and grace periods" (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md "Receipt verification" step 7;
/// the story's acceptance criterion "Renewals, cancellations, refunds and grace periods update entitlements
/// safely").
///
/// IDEMPOTENT (the headline requirement, docs/21 "Store notifications must be idempotent"). Idempotency is
/// two-layered:
/// <list type="number">
///   <item>The DEDUP LEDGER. A notification is named by the (provider, provider notification id) pair; an inbound
///   notification whose pair is already in <c>store_notification_events</c> is recognized and returned
///   <see cref="StoreNotificationProcessingOutcome.AlreadyProcessed"/> with no further effect — a re-delivered or
///   replayed notification changes nothing twice (the unique
///   <c>store_notification_events(provider, provider_notification_id)</c> index also catches a concurrent race).</item>
///   <item>The IDEMPOTENT EFFECT. The purchase status change it drives REUSES
///   <see cref="PurchaseTransactionService.ChangeStatusAsync"/> (CORE-STORE-002), which is itself idempotent (a
///   change to the status the purchase is already in writes no purchase event), so even two notifications that
///   slip past the dedup concurrently apply at most one real change and one audit event.</item>
/// </list>
///
/// SAFE ENTITLEMENT UPDATE. The notification's <see cref="StoreNotification.Type"/> maps to exactly one target
/// <see cref="PurchaseTransactionStatus"/> (<see cref="StoreNotificationTypeExtensions.ToTransactionStatus"/>): a
/// renewal keeps/reactivates the purchase <see cref="PurchaseTransactionStatus.Active"/>, a cancellation downgrades
/// it to <see cref="PurchaseTransactionStatus.Cancelled"/>, a refund revokes it to
/// <see cref="PurchaseTransactionStatus.Refunded"/>, and a grace period moves it to the explicit
/// <see cref="PurchaseTransactionStatus.InGracePeriod"/> state ("Refunds and chargebacks must revoke or downgrade
/// entitlements"; "Grace periods must be represented explicitly", docs/21). The persisted purchase status is the
/// server-side source of truth for premium state ("User-visible premium state must come from server
/// entitlements", docs/21), so updating it IS the safe entitlement update; granting/revoking the linked
/// <c>SubjectEntitlement</c> (which requires the buyer linkage, the separate <c>billing_account_links</c> table)
/// is a later story that consumes this same status as its trigger.
///
/// FAIL-CLOSED. A notification for a purchase Core never recorded is
/// <see cref="StoreNotificationProcessingOutcome.TransactionNotFound"/> — nothing is fabricated — but the
/// notification's arrival is still recorded so it is auditable and not reprocessed.
///
/// AUTHORIZATION IS UPSTREAM. The notification endpoint resolves the deployment-supplied parser and validates the
/// payload's signature/source BEFORE this service is invoked; this service performs the dedup, the purchase status
/// change and the audit only, and is only ever handed an already-validated, normalized notification.
///
/// RECONCILIATION (CORE-JOB-003). Because <see cref="HandleAsync"/> applies notifications in delivery order, a
/// store's at-least-once, possibly-reordered delivery can leave the persisted purchase status drifted from the
/// status the latest-by-event-time notification implies. <see cref="ReconcileTransactionAsync"/> re-derives the
/// converged status from the ledger this service already wrote (the latest <see cref="StoreNotificationEvent"/> by
/// <see cref="StoreNotificationEvent.OccurredAt"/>) and converges the purchase by REUSING the same idempotent,
/// audited <see cref="PurchaseTransactionService.ChangeStatusAsync"/> — so the reconciliation job reuses this
/// service rather than building a parallel pipeline.
/// </summary>
public sealed class StoreNotificationService
{
    private readonly IStoreNotificationEventRepository _notifications;
    private readonly PurchaseTransactionService _transactions;

    public StoreNotificationService(
        IStoreNotificationEventRepository notifications,
        PurchaseTransactionService transactions)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(transactions);
        _notifications = notifications;
        _transactions = transactions;
    }

    /// <summary>
    /// Handles a validated, normalized store notification: deduplicates it by its (provider, provider notification
    /// id) pair, drives the affected purchase's lifecycle status (idempotently and auditably, reusing
    /// CORE-STORE-002) and records the notification as an auditable ledger fact. An already-handled notification is
    /// an idempotent no-op (<see cref="StoreNotificationProcessingOutcome.AlreadyProcessed"/>).
    /// </summary>
    /// <param name="notification">The validated, normalized notification to handle.</param>
    /// <param name="receivedAt">When the notification was received.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="notification"/> is null.</exception>
    public async Task<StoreNotificationProcessingResult> HandleAsync(
        StoreNotification notification,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Layer 1 — dedup: a notification already in the ledger is recognized and ignored with no further effect
        // (idempotent). This is the fast path for a store's at-least-once re-delivery.
        var existing = await _notifications
            .FindByProviderNotificationAsync(notification.Provider, notification.ProviderNotificationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new StoreNotificationProcessingResult(StoreNotificationProcessingOutcome.AlreadyProcessed);
        }

        // Drive the affected purchase's lifecycle. The notification's type maps to exactly one target status; the
        // change is audited and idempotent (CORE-STORE-002). The store's reported event time backs the purchase
        // event, while the ledger row records when we received the notification.
        var targetStatus = notification.Type.ToTransactionStatus();
        var changeOutcome = await _transactions
            .ChangeStatusAsync(
                notification.Provider,
                notification.ProviderTransactionId,
                targetStatus,
                notification.OccurredAt,
                cancellationToken)
            .ConfigureAwait(false);

        var outcome = changeOutcome switch
        {
            PurchaseStatusChangeOutcome.Changed => StoreNotificationProcessingOutcome.Applied,
            PurchaseStatusChangeOutcome.Unchanged => StoreNotificationProcessingOutcome.Unchanged,
            PurchaseStatusChangeOutcome.TransactionNotFound => StoreNotificationProcessingOutcome.TransactionNotFound,
            _ => throw new InvalidOperationException($"Unhandled purchase status change outcome '{changeOutcome}'."),
        };

        // Record the notification's arrival and effect as an auditable, append-only ledger fact. This also claims
        // the (provider, provider notification id) so a later re-delivery is deduplicated. A concurrent handler
        // that won the race already recorded it: the unique index rejects this insert, and we report the
        // idempotent AlreadyProcessed (the status change we drove above was itself idempotent, so no double effect).
        var notificationEvent = StoreNotificationEvent.Record(notification, outcome, receivedAt);
        var addResult = await _notifications.AddAsync(notificationEvent, cancellationToken).ConfigureAwait(false);
        if (addResult == StoreNotificationEventAddResult.DuplicateNotification)
        {
            return new StoreNotificationProcessingResult(StoreNotificationProcessingOutcome.AlreadyProcessed);
        }

        return new StoreNotificationProcessingResult(outcome);
    }

    /// <summary>
    /// Reconciles ONE purchase against its recorded store notifications so its persisted status converges on the
    /// status the AUTHORITATIVE (latest-by-event-time) notification implies (CORE-JOB-003, the store-notification
    /// reconciliation job of the "Worker Background Jobs" epic). The synchronous webhook (<see cref="HandleAsync"/>)
    /// applies notifications in DELIVERY order, but a store delivers at least once and can reorder or drop
    /// deliveries, so the persisted status can drift; this re-derives the correct status from the ledger and
    /// (only if it differs) drives the purchase to it — "missed or out-of-order store notifications are reconciled
    /// so entitlement state converges" (the story's acceptance criterion).
    ///
    /// <para>
    /// RE-DERIVES FROM THE LEDGER, NOT DELIVERY ORDER. The converged status is the
    /// <see cref="StoreNotificationEvent.AppliedStatus"/> of the recorded notification with the latest
    /// <see cref="StoreNotificationEvent.OccurredAt"/> (the store's reported event time;
    /// <see cref="IStoreNotificationEventRepository.FindLatestByProviderTransactionAsync"/>) — regardless of the
    /// order the notifications were delivered or applied. So an out-of-order delivery (an older notification
    /// applied after a newer one) and a missed delivery (a notification that arrived before the purchase existed,
    /// recorded but never applied, and whose effect is the latest) both converge to the same correct status.
    /// </para>
    ///
    /// <para>
    /// IDEMPOTENT AND REUSES THE WEBHOOK PATH. The status change REUSES
    /// <see cref="PurchaseTransactionService.ChangeStatusAsync"/> — the exact same audited, idempotent status
    /// change the webhook uses — stamped with the authoritative notification's event time. A purchase already at
    /// the converged status is an idempotent no-op (no status change, no audit event), so re-running the sweep
    /// changes nothing.
    /// </para>
    ///
    /// <para>
    /// FAIL-CLOSED. A purchase Core never recorded yields
    /// <see cref="StoreNotificationReconciliationOutcome.TransactionNotFound"/> — nothing is fabricated, so no
    /// entitlement is granted without a real verified purchase behind it. This method neither dedups nor records a
    /// ledger row: it derives from the existing ledger and only converges the purchase, so it is safe to run on a
    /// cadence with the webhook.
    /// </para>
    /// </summary>
    /// <param name="provider">The store that issued the purchase.</param>
    /// <param name="providerTransactionId">The provider-assigned transaction id naming the purchase to reconcile.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <exception cref="ArgumentException">The provider transaction id is blank.</exception>
    public async Task<StoreNotificationReconciliationOutcome> ReconcileTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken)
    {
        var latest = await _notifications
            .FindLatestByProviderTransactionAsync(provider, providerTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            // No recorded notifications for this purchase: nothing to reconcile against.
            return StoreNotificationReconciliationOutcome.NoNotifications;
        }

        // The authoritative status is the one the latest-by-event-time notification drove the purchase to. Stamp
        // the convergence with that notification's event time so the persisted UpdatedAt and the purchase event
        // reflect when the authoritative event actually occurred, not when reconciliation ran.
        var changeOutcome = await _transactions
            .ChangeStatusAsync(provider, providerTransactionId, latest.AppliedStatus, latest.OccurredAt, cancellationToken)
            .ConfigureAwait(false);

        return changeOutcome switch
        {
            PurchaseStatusChangeOutcome.Changed => StoreNotificationReconciliationOutcome.Converged,
            PurchaseStatusChangeOutcome.Unchanged => StoreNotificationReconciliationOutcome.AlreadyConsistent,
            PurchaseStatusChangeOutcome.TransactionNotFound => StoreNotificationReconciliationOutcome.TransactionNotFound,
            _ => throw new InvalidOperationException($"Unhandled purchase status change outcome '{changeOutcome}'."),
        };
    }
}
