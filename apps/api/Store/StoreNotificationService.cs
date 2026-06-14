using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

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
/// MONOTONIC, REVOKED-STAYS-REVOKED (CORE-MON-004). The purchase status change goes through the monotonic state
/// machine (<see cref="PurchaseTransaction.ChangeStatus"/> / <see cref="PurchaseTransactionStatusMachine"/>): a
/// revoked state (<see cref="PurchaseTransactionStatus.Refunded"/> / <see cref="PurchaseTransactionStatus.Cancelled"/>)
/// is absorbing, so a renewal arriving AFTER a refund can never flip the purchase back to
/// <see cref="PurchaseTransactionStatus.Active"/>. When a notification drives the purchase INTO a revoked state, the
/// granted entitlement is revoked through the optional <see cref="PurchaseEntitlementRevocationService"/> (the
/// inverse of the CORE-MON-003 grant chain) BEFORE the status change is committed, so a failed revocation leaves
/// the work unfinished and a re-delivery/sweep retries it ("Refunds and chargebacks must revoke or downgrade
/// entitlements", docs/21). The revocation collaborator is wired by the API and worker hosts; it is null only in
/// focused unit tests of the status machine, where the always-on domain guard still holds.
///
/// RECONCILIATION (CORE-JOB-003 / CORE-MON-004). Because <see cref="HandleAsync"/> applies notifications in delivery
/// order, a store's at-least-once, possibly-reordered delivery can leave the persisted purchase status drifted from
/// the status the recorded notifications imply. <see cref="ReconcileTransactionAsync"/> re-derives the converged
/// status by FOLDING the monotonic state machine over ALL of the purchase's recorded notifications in event-time
/// order (<see cref="PurchaseTransactionStatusMachine.Converge"/>) — not just the single latest one — so a later
/// renewal recorded after a refund cannot resurrect the purchase through reconciliation either. It converges the
/// purchase by REUSING the same idempotent, audited <see cref="PurchaseTransactionService.ChangeStatusAsync"/> and
/// the same revocation path, so the reconciliation job reuses this service rather than building a parallel pipeline.
///
/// ATOMIC APPLY+LEDGER (CORE-MON-010). <see cref="HandleAsync"/> applies the purchase status change AND writes its
/// dedup-ledger row in ONE database transaction (reusing the CORE-CONC-002 <see cref="TransactionalUnitOfWork"/>).
/// Before this the status change committed first (its own <c>SaveChanges</c> through
/// <see cref="PurchaseTransactionService.ChangeStatusAsync"/>) and only THEN the
/// <c>store_notification_events</c> dedup row was inserted, in SEPARATE transactions — so a crash between them left
/// the status changed but the notification unrecorded, and the store's at-least-once RE-DELIVERY re-applied it and
/// could double-append the <c>purchase_events</c> audit trail. Wrapping both (and, for a revoking notification, the
/// entitlement revocation that precedes them) in one transaction makes a part-way failure roll EVERYTHING back: a
/// re-delivery then either finds the ledger row and is a deduplicated no-op, or replays the whole effect from
/// scratch — never a status applied without its first-arrival record, never a duplicated audit entry. The dedup
/// fast-path read stays OUTSIDE the transaction (it only short-circuits a known re-delivery; the unique
/// <c>store_notification_events(provider, provider_notification_id)</c> index is the real race guard, inside).
/// </summary>
public sealed class StoreNotificationService
{
    private readonly IStoreNotificationEventRepository _notifications;
    private readonly PurchaseTransactionService _transactions;
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly PurchaseEntitlementRevocationService? _revocation;
    private readonly IAuditLogRepository? _auditLog;

    /// <param name="notifications">The dedup ledger repository for the (provider, provider notification id) idempotency key.</param>
    /// <param name="transactions">The reused, idempotent, audited purchase status-change service (CORE-STORE-002).</param>
    /// <param name="unitOfWork">The transactional unit of work that makes apply + ledger atomic (CORE-MON-010).</param>
    /// <param name="revocation">
    /// The entitlement revocation collaborator (CORE-MON-004) invoked before a revoking notification's status
    /// change; null only in focused unit tests of the status machine.
    /// </param>
    /// <param name="auditLog">
    /// The append-only audit log that records the store-notification audit facts (CORE-SPEC-002:
    /// <see cref="AuditAction.StoreNotificationReceived"/> on first arrival and
    /// <see cref="AuditAction.StoreNotificationProcessed"/> when the effect is applied — the catalog's
    /// StoreNotificationReceived/Processed events). OPTIONAL: the API and worker hosts inject it so a handled
    /// notification is audited as a PLATFORM-level fact (a store notification is deployment-spanning, not
    /// tenant-scoped — docs/21); it is null in focused unit tests of the dedup/status machine. Only
    /// <see cref="HandleAsync"/> emits these (reconciliation receives no notification and writes no ledger row).
    /// </param>
    public StoreNotificationService(
        IStoreNotificationEventRepository notifications,
        PurchaseTransactionService transactions,
        TransactionalUnitOfWork unitOfWork,
        PurchaseEntitlementRevocationService? revocation = null,
        IAuditLogRepository? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(transactions);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _notifications = notifications;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _revocation = revocation;
        _auditLog = auditLog;
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

        // FIRST ARRIVAL: record the notification's receipt as a real audit fact (CORE-SPEC-002:
        // AuditAction.StoreNotificationReceived). A re-delivery short-circuits above (dedup), so this is recorded
        // once per genuinely-new arrival, never on a deduplicated replay. It is a PLATFORM-level fact (a store
        // notification is deployment-spanning, not tenant-scoped — docs/21): null organization, no actor, and the
        // generic notification type as the descriptor — never the payload or receipt content (threat T7).
        if (_auditLog is not null)
        {
            await _auditLog
                .AppendAsync(
                    AuditLogEntry.ForStoreNotificationReceived(notification.Type.ToString(), receivedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Drive the affected purchase's lifecycle. The notification's type maps to exactly one target status; the
        // change is audited and idempotent (CORE-STORE-002), and goes through the monotonic state machine so a
        // renewal after a refund is a no-op (the purchase stays revoked). The store's reported event time backs the
        // purchase event, while the ledger row records when we received the notification.
        var targetStatus = notification.Type.ToTransactionStatus();

        // ONE unit of work (CORE-MON-010, reusing the CORE-CONC-002 TransactionalUnitOfWork): the entitlement
        // revocation, the purchase STATUS CHANGE and the dedup-LEDGER write commit together or roll back together.
        // Before this the status change and the ledger row were separate transactions, so a crash between them left
        // the status applied without its first-arrival record and a re-delivery re-applied it — double-appending the
        // purchase_events trail. Now a part-way failure rolls the whole effect back, so a re-delivery either dedups
        // (the ledger row is present) or replays the whole effect from scratch. Every repository writes through the
        // SAME scoped DbContext this unit of work begins the transaction on, so each SaveChanges enrols in the one
        // transaction; it is opened inside the EF execution strategy's ExecuteAsync, so it stays correct under the
        // CORE-CONC-003 retrying strategy.
        return await _unitOfWork
            .ExecuteAsync(
                async unitToken =>
                {
                    // On a revoking notification (refund/cancellation/chargeback) revoke the granted entitlement
                    // BEFORE the status change (CORE-MON-004; "on entering a revoked state revoke the granted
                    // entitlement"). Inside this transaction a revoke failure rolls back the status change and the
                    // ledger write too, so the store's re-delivery retries the whole effect; the revoke is
                    // idempotent, so the retry converges. An unrecorded/unlinked purchase or an unmapped product
                    // revokes nothing (fail-closed).
                    if (PurchaseTransactionStatusMachine.IsRevoked(targetStatus) && _revocation is not null)
                    {
                        await _revocation
                            .RevokeForPurchaseAsync(
                                notification.Provider,
                                notification.ProviderTransactionId,
                                notification.OccurredAt,
                                unitToken)
                            .ConfigureAwait(false);
                    }

                    var changeOutcome = await _transactions
                        .ChangeStatusAsync(
                            notification.Provider,
                            notification.ProviderTransactionId,
                            targetStatus,
                            notification.OccurredAt,
                            unitToken)
                        .ConfigureAwait(false);

                    var outcome = changeOutcome switch
                    {
                        PurchaseStatusChangeOutcome.Changed => StoreNotificationProcessingOutcome.Applied,
                        PurchaseStatusChangeOutcome.Unchanged => StoreNotificationProcessingOutcome.Unchanged,
                        PurchaseStatusChangeOutcome.TransactionNotFound => StoreNotificationProcessingOutcome.TransactionNotFound,
                        _ => throw new InvalidOperationException($"Unhandled purchase status change outcome '{changeOutcome}'."),
                    };

                    // Record the notification's arrival and effect as an auditable, append-only ledger fact in the
                    // SAME transaction as the status change above. This also claims the (provider, provider
                    // notification id) so a later re-delivery is deduplicated. A concurrent handler that won the race
                    // already recorded it: the unique index rejects this insert, and we report the idempotent
                    // AlreadyProcessed (the status change we drove above was itself idempotent, so no double effect).
                    var notificationEvent = StoreNotificationEvent.Record(notification, outcome, receivedAt);
                    var addResult = await _notifications.AddAsync(notificationEvent, unitToken).ConfigureAwait(false);
                    if (addResult == StoreNotificationEventAddResult.DuplicateNotification)
                    {
                        return new StoreNotificationProcessingResult(StoreNotificationProcessingOutcome.AlreadyProcessed);
                    }

                    // Record that the notification was idempotently applied as a real audit fact (CORE-SPEC-002:
                    // AuditAction.StoreNotificationProcessed). It is written INSIDE this unit of work, so it commits
                    // or rolls back atomically with the status change and the dedup-ledger row (CORE-MON-010): a
                    // part-way failure leaves no Processed fact, and a replay (above) emits none. PLATFORM-level
                    // (null organization, no actor); the applied outcome is the descriptor (threat T7).
                    if (_auditLog is not null)
                    {
                        await _auditLog
                            .AppendAsync(
                                AuditLogEntry.ForStoreNotificationProcessed(outcome.ToString(), receivedAt),
                                unitToken)
                            .ConfigureAwait(false);
                    }

                    return new StoreNotificationProcessingResult(outcome);
                },
                cancellationToken)
            .ConfigureAwait(false);
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
    /// RE-DERIVES FROM THE LEDGER BY A MONOTONIC FOLD, NOT DELIVERY ORDER. The converged status is the MONOTONIC
    /// FOLD of ALL the purchase's recorded notifications in event-time order
    /// (<see cref="PurchaseTransactionStatusMachine.Converge"/> over
    /// <see cref="IStoreNotificationEventRepository.ListByProviderTransactionAsync"/>) — not merely the single
    /// latest-by-event-time notification. So an out-of-order delivery (an older notification applied after a newer
    /// one) and a missed delivery (a notification recorded before the purchase existed, never applied) both converge
    /// to the same correct status, AND a refund stays revoked even when a later renewal was recorded after it — a
    /// later renewal can never resurrect a refund through reconciliation (CORE-MON-004; "reconciliation cannot
    /// resurrect it"). A purchase already in a revoked state is therefore never moved.
    /// </para>
    ///
    /// <para>
    /// REVOKES ON CONVERGING TO A REVOKED STATE. When the converged status is a revoked state, the granted
    /// entitlement is revoked (the optional <see cref="PurchaseEntitlementRevocationService"/>) BEFORE the status
    /// change is committed — so a revoke failure leaves the purchase still drifted and the next sweep retries it
    /// (the revoke is idempotent, so the retry converges). This is the same revocation path the webhook uses for a
    /// missed refund that only the reconciliation job can apply.
    /// </para>
    ///
    /// <para>
    /// IDEMPOTENT AND REUSES THE WEBHOOK PATH. The status change REUSES
    /// <see cref="PurchaseTransactionService.ChangeStatusAsync"/> — the exact same audited, idempotent, monotonic
    /// status change the webhook uses — stamped with the event time of the notification that determined the
    /// converged status. A purchase already at the converged status is an idempotent no-op (no status change, no
    /// audit event), so re-running the sweep changes nothing.
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
        var recorded = await _notifications
            .ListByProviderTransactionAsync(provider, providerTransactionId, cancellationToken)
            .ConfigureAwait(false);
        if (recorded.Count == 0)
        {
            // No recorded notifications for this purchase: nothing to reconcile against.
            return StoreNotificationReconciliationOutcome.NoNotifications;
        }

        // Re-derive the converged status by folding the monotonic state machine over every recorded notification in
        // event-time order. This respects "a revoked state is terminal" regardless of delivery/record order, so a
        // refund stays revoked even when a later renewal was recorded after it.
        var (convergedStatus, determinedAt) = PurchaseTransactionStatusMachine.Converge(
            recorded.Select(notification => (notification.AppliedStatus, notification.OccurredAt, notification.Id)));

        // Stamp the convergence with the event time of the notification that determined the converged status (the
        // authoritative event), not the sweep time. If no notification moved the purchase from Active (all no-ops),
        // fall back to the latest recorded event time; the change is then a no-op anyway.
        var occurredAt = determinedAt ?? recorded.Max(notification => notification.OccurredAt);

        // Revoke the granted entitlement BEFORE converging the status when the converged status is revoked, so a
        // revoke failure leaves the purchase drifted for the next sweep to retry (the revoke is idempotent).
        if (PurchaseTransactionStatusMachine.IsRevoked(convergedStatus) && _revocation is not null)
        {
            await _revocation
                .RevokeForPurchaseAsync(provider, providerTransactionId, occurredAt, cancellationToken)
                .ConfigureAwait(false);
        }

        var changeOutcome = await _transactions
            .ChangeStatusAsync(provider, providerTransactionId, convergedStatus, occurredAt, cancellationToken)
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
