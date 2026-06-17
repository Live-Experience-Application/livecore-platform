// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// The background store-notification reconciliation job's application service (CORE-JOB-003, the store-notification
/// reconciliation story of the "Worker Background Jobs" epic). Store notifications are processed only on the
/// synchronous inbound webhook (CORE-STORE-005), in DELIVERY order — but a store delivers at least once and can
/// reorder or drop deliveries, so a purchase's persisted status can drift from the status the latest-by-event-time
/// notification implies. This service runs a periodic sweep that re-derives each drifted purchase's status from the
/// recorded ledger and converges it, so "missed or out-of-order store notifications are reconciled so entitlement
/// state converges; idempotent" (the story's acceptance criterion). The scheduling host — the background worker —
/// invokes it on an interval (docs/02_ARCHITECTURE.md: the worker owns async jobs), exactly as it invokes the
/// recap generation and export processing services; the service itself is host-agnostic and fully unit-testable.
///
/// <para>
/// REUSES <see cref="StoreNotificationService"/> (the story note: "Reuse StoreNotificationService"). The
/// per-purchase re-derivation and convergence is <see cref="StoreNotificationService.ReconcileTransactionAsync"/>,
/// which itself reuses the CORE-STORE-002 <see cref="PurchaseTransactionService.ChangeStatusAsync"/> — the same
/// audited, idempotent status change the webhook drives. No parallel reconciliation pipeline is built. The drifted
/// purchases come from <see cref="IReconcilablePurchaseReader"/> (the candidate set shrinks as work completes, so a
/// bounded sweep makes progress).
/// </para>
///
/// <para>
/// IDEMPOTENT and CRASH-SAFE. Reconciliation re-derives from immutable ledger facts and converges to the same
/// state every time: a purchase already consistent is a no-op (no status change, no audit event), and a converged
/// purchase drops out of the candidate set, so repeated sweeps — or a sweep retried after a crash — never
/// double-apply. Each convergence is a single audited status change committed in its own unit of work.
/// </para>
///
/// <para>
/// FAIL-CLOSED and GLOBAL. Purchases are global, keyed only by (provider, provider transaction id) with no tenant
/// or buyer column (CORE-STORE-002), so there is no tenant boundary to scope here — this is a system job, not a
/// tenant actor. Nothing is ever fabricated: a notification recorded for a purchase Core never persisted converges
/// nothing (<see cref="StoreNotificationReconciliationOutcome.TransactionNotFound"/>), so no entitlement is granted
/// without a real verified purchase. The job runs at all only when a deployment has enabled it
/// (<see cref="StoreNotificationReconciliationOptions.Enabled"/>; "only runs when billing is configured").
/// </para>
///
/// <para>
/// RESILIENCE. The sweep is per-purchase resilient: a purchase whose reconciliation fails is logged and counted,
/// and that purchase is left unchanged — no status change was committed — so the next sweep retries it, rather than
/// aborting the whole run. This explicitly includes an OPTIMISTIC-CONCURRENCY CONFLICT (CORE-CONC-007): the
/// purchase's read-modify-write goes through the same token-bearing model the HTTP path uses, but the worker has no
/// <c>ConcurrencyConflictMiddleware</c> to turn a <see cref="DbUpdateConcurrencyException"/> into a 409, so the loop
/// catches it here, logs it, and skips-and-continues — a concurrent change to one purchase never surfaces as an
/// unhandled job exception nor poisons the rest of the batch (the repository detaches the conflicted row from the
/// shared sweep context). All logging is identifier-only (the provider and counts, never the transaction id, a
/// buyer or any content), so a sweep is safe to log (threat T7).
/// </para>
/// </summary>
public sealed class StoreNotificationReconciliationService
{
    private readonly IReconcilablePurchaseReader _reconcilablePurchases;
    private readonly StoreNotificationService _storeNotifications;
    private readonly StoreNotificationReconciliationOptions _options;
    private readonly ILogger<StoreNotificationReconciliationService> _logger;

    /// <summary>Creates the reconciliation service over the candidate reader, the reused notification service and the policy.</summary>
    public StoreNotificationReconciliationService(
        IReconcilablePurchaseReader reconcilablePurchases,
        StoreNotificationService storeNotifications,
        StoreNotificationReconciliationOptions options,
        ILogger<StoreNotificationReconciliationService> logger)
    {
        ArgumentNullException.ThrowIfNull(reconcilablePurchases);
        ArgumentNullException.ThrowIfNull(storeNotifications);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _reconcilablePurchases = reconcilablePurchases;
        _storeNotifications = storeNotifications;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs one reconciliation sweep: finds up to <see cref="StoreNotificationReconciliationOptions.BatchSize"/>
    /// drifted purchases (those whose persisted status differs from the status their latest recorded notification
    /// implies) and converges each by re-deriving its status from the ledger, returning the run's count-only
    /// summary. Idempotent (a consistent purchase is never drifted and a converged purchase drops out of the
    /// candidate set) and resilient (a per-purchase failure is logged and that purchase is left for the next sweep
    /// to retry, rather than aborting the run).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many drifted purchases were examined, reconciled and failed.</returns>
    public async Task<StoreNotificationReconciliationResult> ReconcileDriftedPurchasesAsync(CancellationToken cancellationToken)
    {
        var drifted = await _reconcilablePurchases
            .ListReconcilablePurchasesAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (drifted.Count == 0)
        {
            return new StoreNotificationReconciliationResult(Examined: 0, Reconciled: 0, Failed: 0);
        }

        var examined = 0;
        var reconciled = 0;
        var failed = 0;

        foreach (var purchase in drifted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            try
            {
                var outcome = await _storeNotifications
                    .ReconcileTransactionAsync(purchase.Provider, purchase.ProviderTransactionId, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome == StoreNotificationReconciliationOutcome.Converged)
                {
                    reconciled++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Host shutdown during the sweep is expected; stop cleanly and let the loop end.
                throw;
            }
            catch (DbUpdateConcurrencyException exception)
            {
                // A concurrent writer changed this purchase between the reconciliation read and its
                // read-modify-write (the xmin token, CORE-CONC-001/006, raised the conflict rather than a silent
                // last-write-wins). In an HTTP request this surfaces as a 409 (ConcurrencyConflictMiddleware), but a
                // worker sweep has no such middleware, so we handle it HERE (CORE-CONC-007): no status change was
                // committed, the repository detached the conflicted row so the rest of this batch is unaffected, and
                // the purchase is left drifted for the next sweep to retry — a skip-and-continue, never an unhandled
                // job exception that tears the loop down. Counted as a failed convergence and logged with the
                // provider only, never the transaction id or any content (threat T7).
                failed++;
                _logger.LogWarning(
                    exception,
                    "A concurrent change conflicted while reconciling a {Provider} purchase; it stays drifted and will be retried on the next sweep.",
                    purchase.Provider);
            }
            catch (DbUpdateException exception)
            {
                // A convergence failed to persist (a transient persistence error). No status change was committed
                // and the purchase is left unchanged, so the next sweep retries it, and we move on. The message
                // carries only the provider, never the transaction id or any content (threat T7).
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to reconcile a {Provider} purchase; it stays drifted and will be retried on the next sweep.",
                    purchase.Provider);
            }
        }

        if (reconciled > 0)
        {
            _logger.LogInformation(
                "Store notification reconciliation sweep complete: examined {Examined}, reconciled {Reconciled}, failed {Failed}.",
                examined,
                reconciled,
                failed);
        }

        return new StoreNotificationReconciliationResult(Examined: examined, Reconciled: reconciled, Failed: failed);
    }
}
