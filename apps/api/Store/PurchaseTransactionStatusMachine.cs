namespace LiveCore.Api.Store;

/// <summary>
/// The MONOTONIC state machine of a <see cref="PurchaseTransaction"/>'s lifecycle (CORE-MON-004, the
/// monotonic-purchase-status story of the "Monetization v1" epic). It is the single, product-neutral place that
/// decides which purchase status transitions are permitted, so the synchronous webhook
/// (<see cref="StoreNotificationService.HandleAsync"/>), the persisted aggregate
/// (<see cref="PurchaseTransaction.ChangeStatus"/>) and the reconciliation re-derivation
/// (<see cref="StoreNotificationService.ReconcileTransactionAsync"/> / <see cref="ReconcilablePurchaseReader"/>)
/// can never disagree about whether a refund "stays revoked".
///
/// THE STORY'S ACCEPTANCE CRITERION — "A refunded/cancelled/charged-back purchase revokes the granted entitlement
/// and CANNOT be reactivated by a later renewal/notification; a revoked state is terminal (absorbing)." A REVOKED
/// state (<see cref="PurchaseTransactionStatus.Refunded"/> — which a refund or chargeback drives — and
/// <see cref="PurchaseTransactionStatus.Cancelled"/>) is ABSORBING: once a purchase is in it, no later
/// notification of any kind (a renewal back to <see cref="PurchaseTransactionStatus.Active"/>, a grace period, or
/// even the other revoked kind) moves it. The non-revoked states (<see cref="PurchaseTransactionStatus.Active"/>
/// and <see cref="PurchaseTransactionStatus.InGracePeriod"/>) still transition freely between each other and into a
/// revoked state, so a legitimate grace-period → renewal recovery is unaffected; only re-activation OUT of a
/// revoked state is forbidden. Without this guard a legitimate but later-occurring renewal could flip
/// <c>Refunded → Active</c> and silently re-grant premium to a refunded buyer (docs/21 "Refunds and chargebacks
/// must revoke or downgrade entitlements").
///
/// PURE AND ORDER-INDEPENDENT. The machine is a pure function of (current status, target status), so reconciliation
/// can FOLD it over a purchase's recorded notification effects in event-time order (<see cref="Converge"/>) to
/// re-derive the one converged status the purchase must hold — independent of the order the store delivered the
/// notifications. Because a revoked state is absorbing, the converged status is the revoked status of the
/// EARLIEST-by-event-time revoking notification if any exists, otherwise the latest non-revoking notification's
/// status — so a later renewal can never resurrect a refund through reconciliation either.
/// </summary>
public static class PurchaseTransactionStatusMachine
{
    /// <summary>
    /// Whether a status is a REVOKED (terminal/absorbing) state — the states a refund, chargeback or cancellation
    /// drives a purchase to, in which the granted entitlement is revoked and must stay revoked. These are the
    /// absorbing states of the monotonic machine: no transition out of them is permitted.
    /// </summary>
    public static bool IsRevoked(PurchaseTransactionStatus status)
        => status is PurchaseTransactionStatus.Refunded or PurchaseTransactionStatus.Cancelled;

    /// <summary>
    /// Whether the monotonic machine permits moving a purchase from <paramref name="current"/> to
    /// <paramref name="target"/>. A move to the SAME status is always permitted (an idempotent no-op). Any other
    /// move is permitted only while the purchase is NOT already in a revoked (absorbing) state — so once a purchase
    /// is <see cref="PurchaseTransactionStatus.Refunded"/> or <see cref="PurchaseTransactionStatus.Cancelled"/> it
    /// can never leave that state (the revoked state is terminal).
    /// </summary>
    public static bool CanTransitionTo(PurchaseTransactionStatus current, PurchaseTransactionStatus target)
        => current == target || !IsRevoked(current);

    /// <summary>
    /// Re-derives the one converged status a purchase must hold by FOLDING the monotonic machine over its recorded
    /// notification effects, starting from the <see cref="PurchaseTransactionStatus.Active"/> state a verified
    /// purchase is recorded in. Each effect is the status a recorded notification implied
    /// (<see cref="StoreNotificationEvent.AppliedStatus"/>); the effects are applied in event-time order
    /// (<see cref="StoreNotificationEvent.OccurredAt"/> ascending, ties broken by the time-ordered row id), and a
    /// transition the machine forbids (a re-activation out of a revoked state) is skipped — so the result respects
    /// monotonicity regardless of the order the notifications were delivered or applied.
    /// </summary>
    /// <param name="effects">
    /// The purchase's recorded notification effects, each carrying the status it implied, the store's reported event
    /// time and the time-ordered row id used to break ties. The sequence may be in any order; this method orders it.
    /// </param>
    /// <returns>
    /// The converged status and, when a real transition occurred, the event time of the notification that
    /// determined it (so the convergence can be stamped with the authoritative event's time, not the sweep time);
    /// <see langword="null"/> when no notification moved the purchase from its initial <c>Active</c> state.
    /// </returns>
    public static (PurchaseTransactionStatus Status, DateTimeOffset? DeterminedAt) Converge(
        IEnumerable<(PurchaseTransactionStatus AppliedStatus, DateTimeOffset OccurredAt, Guid Id)> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        var status = PurchaseTransactionStatus.Active;
        DateTimeOffset? determinedAt = null;

        foreach (var effect in effects
            .OrderBy(effect => effect.OccurredAt)
            .ThenBy(effect => effect.Id))
        {
            // A real move only happens when the target differs AND the machine permits it (the current state is not
            // already absorbing). Once a revoking effect lands, every later effect is a forbidden re-activation and
            // is skipped, so the absorbing state sticks.
            if (effect.AppliedStatus != status && CanTransitionTo(status, effect.AppliedStatus))
            {
                status = effect.AppliedStatus;
                determinedAt = effect.OccurredAt;
            }
        }

        return (status, determinedAt);
    }
}
