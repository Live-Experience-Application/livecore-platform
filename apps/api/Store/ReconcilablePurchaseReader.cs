using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// EF Core implementation of <see cref="IReconcilablePurchaseReader"/> (CORE-JOB-003 / CORE-MON-004). It returns
/// the persisted purchases whose current <see cref="PurchaseTransaction.Status"/> differs from the status the
/// MONOTONIC FOLD of their recorded notifications implies (<see cref="PurchaseTransactionStatusMachine.Converge"/>)
/// — the drift a missed or out-of-order store delivery leaves behind. A purchase ALREADY in a revoked (terminal)
/// state is never reconcilable: a revoked state is absorbing, so it can neither drift nor be reconciled away — and
/// using the same monotonic fold the convergence uses keeps the candidate set and the convergence in agreement, so
/// a later renewal recorded after a refund never re-flags the refunded purchase as drifted (which would otherwise
/// spin the bounded sweep forever). A purchase with no notifications is excluded (nothing to reconcile against).
///
/// <para>
/// LATEST-BY-EVENT-TIME IS COMPUTED CLIENT-SIDE. Determining the latest notification cannot be expressed in SQL
/// here: the SQLite provider used in the unit-test harness cannot ORDER BY or aggregate a DateTimeOffset column,
/// and a triply-correlated "no later notification" subquery does not translate. So the reader runs two translatable
/// single-level queries (the purchases that have notifications, and those purchases' notifications) and picks the
/// latest per purchase in memory. Store receipts/billing are out of scope for Core v1 and this job is OFF by
/// default (gated on <c>Store:Reconciliation:Enabled</c>), so the ledger is low-volume; a SQL window-function form
/// for high-volume deployments is a documented follow-up.
/// </para>
///
/// <para>
/// READ-ONLY (no tracking) and bounded by the batch size. The candidate set SHRINKS as work completes — a
/// reconciled purchase's status now equals its latest notification's applied status, so it no longer drifts —
/// which is why a bounded sweep eventually drains the backlog rather than starving later purchases (mirrors the
/// Exports queued-job reader and the Recaps eligibility reader). Ordered by the (provider, provider transaction id)
/// idempotency key for a deterministic, stable batch.
/// </para>
/// </summary>
internal sealed class ReconcilablePurchaseReader : IReconcilablePurchaseReader
{
    private readonly LiveCoreDbContext _dbContext;

    public ReconcilablePurchaseReader(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReconcilablePurchase>> ListReconcilablePurchasesAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        // A purchase needs reconciliation when the MONOTONIC FOLD of its recorded notifications (in event-time
        // order) implies a status that differs from the purchase's current status — the drift a missed or
        // out-of-order delivery leaves behind. The fold cannot be pushed into SQL here: the SQLite provider used in
        // the test harness cannot ORDER BY or aggregate a DateTimeOffset column, and the monotonic fold is a
        // sequential reduction, not a single aggregate. So the converged-per-purchase status is computed client-side
        // from two translatable single-level queries. Store receipts/billing are out of scope for Core v1 and this
        // job is off by default (gated on Store:Reconciliation:Enabled), so the ledger is low-volume; a SQL form for
        // high-volume deployments is a documented follow-up.

        // Purchases that have at least one recorded notification, with their current status (single-level EXISTS).
        var purchases = await _dbContext.PurchaseTransactions
            .AsNoTracking()
            .Where(transaction => _dbContext.StoreNotificationEvents.Any(notification =>
                notification.Provider == transaction.Provider
                && notification.ProviderTransactionId == transaction.ProviderTransactionId))
            .Select(transaction => new
            {
                transaction.Provider,
                transaction.ProviderTransactionId,
                transaction.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (purchases.Count == 0)
        {
            return Array.Empty<ReconcilablePurchase>();
        }

        // Their notifications, enough to pick the latest per purchase client-side (only notifications whose purchase
        // exists, single-level EXISTS — an orphan notification for an unrecorded purchase reconciles nothing).
        var notifications = await _dbContext.StoreNotificationEvents
            .AsNoTracking()
            .Where(notification => _dbContext.PurchaseTransactions.Any(transaction =>
                transaction.Provider == notification.Provider
                && transaction.ProviderTransactionId == notification.ProviderTransactionId))
            .Select(notification => new
            {
                notification.Provider,
                notification.ProviderTransactionId,
                notification.AppliedStatus,
                notification.OccurredAt,
                notification.Id,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Converged status per purchase: the monotonic fold of its notifications in event-time order (the same fold
        // the reconciliation convergence uses), so a revoked state is absorbing and a later renewal recorded after a
        // refund does not change the converged status.
        var convergedStatusByPurchase = notifications
            .GroupBy(notification => (notification.Provider, notification.ProviderTransactionId))
            .ToDictionary(
                group => group.Key,
                group => PurchaseTransactionStatusMachine.Converge(
                    group.Select(notification => (notification.AppliedStatus, notification.OccurredAt, notification.Id))).Status);

        // Keep only the drifted purchases — the converged status differs from the current status — and EXCLUDE any
        // purchase already in a revoked (terminal/absorbing) state: it can neither drift nor be reconciled away, so
        // flagging it would spin the bounded sweep forever. A reconciled purchase then matches and drops out, so a
        // bounded sweep makes progress. Ordered deterministically by the (provider, provider transaction id) key.
        return purchases
            .Where(purchase =>
                !PurchaseTransactionStatusMachine.IsRevoked(purchase.Status)
                && convergedStatusByPurchase.TryGetValue(
                    (purchase.Provider, purchase.ProviderTransactionId), out var convergedStatus)
                && convergedStatus != purchase.Status)
            .OrderBy(purchase => purchase.Provider)
            .ThenBy(purchase => purchase.ProviderTransactionId, StringComparer.Ordinal)
            .Take(batchSize)
            .Select(purchase => new ReconcilablePurchase(purchase.Provider, purchase.ProviderTransactionId))
            .ToList();
    }
}
