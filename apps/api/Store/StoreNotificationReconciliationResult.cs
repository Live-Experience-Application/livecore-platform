namespace LiveCore.Api.Store;

/// <summary>
/// The outcome of one run of the background store-notification reconciliation sweep (CORE-JOB-003). It records
/// counts only — never any identifier or content — so the worker can log a sweep summary safely (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Examined">
/// How many drifted purchases the sweep looked at this run (those whose persisted status differed from the status
/// their latest recorded notification implies).
/// </param>
/// <param name="Reconciled">
/// How many purchases were CONVERGED this run — their status re-derived from the ledger and changed to match the
/// latest-by-event-time notification. Idempotent: a purchase already consistent is never counted, and re-running
/// the sweep over a converged purchase reconciles nothing more.
/// </param>
/// <param name="Failed">
/// How many purchases could not be reconciled this run because a step failed (for example a transient persistence
/// error). Those purchases are left unchanged — no status change was committed — so the next sweep retries them.
/// </param>
public readonly record struct StoreNotificationReconciliationResult(int Examined, int Reconciled, int Failed);
