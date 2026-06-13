namespace LiveCore.Api.Store;

/// <summary>
/// Reads the purchases the reconciliation sweep should re-derive (CORE-JOB-003, the store-notification
/// reconciliation job of the "Worker Background Jobs" epic). A purchase is reconcilable when it is persisted AND
/// its current status differs from the status its latest-by-event-time recorded notification implies — the exact
/// drift a missed or out-of-order store delivery causes (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md;
/// the synchronous webhook applies notifications in delivery order). A purchase the webhook already converged is
/// NOT reconcilable, so once reconciled it drops out of the eligible set and the bounded sweep makes progress —
/// the same "eligible set shrinks as work completes" shape as the queued-export and eligible-recap readers.
///
/// The reader spans all purchases on purpose (it backs a system job, not a tenant actor); purchases are global,
/// keyed only by (provider, provider transaction id), with no tenant or buyer column (CORE-STORE-002), so there
/// is no tenant scoping to apply here.
/// </summary>
public interface IReconcilablePurchaseReader
{
    /// <summary>
    /// Lists up to <paramref name="batchSize"/> purchases whose persisted status differs from the status their
    /// latest-by-event-time recorded notification implies, ordered deterministically so a bounded sweep makes
    /// progress.
    /// </summary>
    /// <param name="batchSize">The maximum number of reconcilable purchases to return (must be positive).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reconcilable purchases (empty when none need reconciliation).</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    Task<IReadOnlyList<ReconcilablePurchase>> ListReconcilablePurchasesAsync(int batchSize, CancellationToken cancellationToken);
}
