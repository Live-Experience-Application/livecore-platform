namespace LiveCore.Api.Store;

/// <summary>
/// The outcome of reconciling ONE purchase against its recorded store notifications (CORE-JOB-003, the
/// store-notification reconciliation job of the "Worker Background Jobs" epic). Store notifications are applied
/// synchronously on the inbound webhook in DELIVERY order, but a store delivers at least once and can reorder or
/// drop deliveries, so the persisted purchase status can drift from the status the latest-by-event-time
/// notification implies. <see cref="StoreNotificationService.ReconcileTransactionAsync"/> re-derives the converged
/// status from the ledger and reports which of these happened.
/// </summary>
public enum StoreNotificationReconciliationOutcome
{
    /// <summary>
    /// The purchase had no recorded store notifications, so there was nothing to reconcile against (its status
    /// stays as recorded). An idempotent no-op.
    /// </summary>
    NoNotifications = 0,

    /// <summary>
    /// The purchase status already matched the status the latest-by-event-time notification implies — the
    /// synchronous webhook had already converged it. An idempotent no-op (no status change, no audit event), so a
    /// re-run of the reconciliation sweep over an already-consistent purchase changes nothing.
    /// </summary>
    AlreadyConsistent = 1,

    /// <summary>
    /// The purchase status differed from the status the latest-by-event-time notification implies (a missed or
    /// out-of-order delivery) and was CONVERGED to it. The change is audited on the <c>purchase_events</c> trail
    /// exactly as a webhook-driven change is (CORE-STORE-002).
    /// </summary>
    Converged = 2,

    /// <summary>
    /// The ledger recorded notifications for a purchase Core never persisted (for example a notification that
    /// arrived before — and only — the purchase was ever verified). Nothing is fabricated (fail-closed): no
    /// purchase row is created, so no entitlement is granted without a real verified purchase behind it.
    /// </summary>
    TransactionNotFound = 3,
}
