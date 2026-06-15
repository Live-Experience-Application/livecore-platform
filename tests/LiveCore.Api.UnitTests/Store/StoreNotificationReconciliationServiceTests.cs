using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the background store-notification reconciliation job (CORE-JOB-003) — the headline
/// behavior the story's required tests pin: "duplicate/missed notification reconciled safely, idempotent", plus the
/// acceptance criterion "missed or out-of-order store notifications are reconciled so entitlement state converges".
/// They run the real <see cref="StoreNotificationReconciliationService"/> over the real collaborators — the
/// <see cref="ReconcilablePurchaseReader"/>, the reused <see cref="StoreNotificationService"/> and the Store
/// repositories — all sharing one <see cref="LiveCoreDbContext"/> over an in-memory SQLite database with foreign
/// keys enforced, exactly as the worker's per-sweep dependency-injection scope wires them (the same harness the
/// other Store data-layer tests use).
///
/// <para>
/// The synchronous webhook (<see cref="StoreNotificationService.HandleAsync"/>) applies notifications in DELIVERY
/// order; these tests deliberately deliver them out of order, or before the purchase exists, so the webhook leaves
/// the persisted status drifted, then assert the reconciliation sweep re-derives the converged status from the
/// recorded ledger (the latest-by-event-time notification) and is an idempotent no-op once converged. Fail-closed:
/// a notification for a purchase Core never recorded fabricates nothing. All fixtures are generic Store vocabulary
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </para>
/// </summary>
public sealed class StoreNotificationReconciliationServiceTests : IDisposable
{
    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _earlierEvent = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _laterEvent = new(2026, 6, 12, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _receivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public StoreNotificationReconciliationServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // Each call models its own request/sweep scope: a fresh context with the module services over it.
    private async Task RecordPurchaseAsync(PurchaseProvider provider, string transactionId)
    {
        await using var context = CreateContext();
        var service = new PurchaseTransactionService(
            new PurchaseTransactionRepository(context),
            new PurchaseEventRepository(context));
        await service.RecordVerifiedPurchaseAsync(
            VerifiedPurchase.Create(provider, transactionId, "product.premium"),
            _recordedAt,
            CancellationToken.None);
    }

    private async Task<StoreNotificationProcessingResult> HandleAsync(StoreNotification notification)
    {
        await using var context = CreateContext();
        var service = new StoreNotificationService(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context));
        return await service.HandleAsync(notification, _receivedAt, CancellationToken.None);
    }

    private async Task<StoreNotificationReconciliationResult> ReconcileSweepAsync(int batchSize = 50)
    {
        await using var context = CreateContext();
        var service = new StoreNotificationReconciliationService(
            new ReconcilablePurchaseReader(context),
            new StoreNotificationService(
                new StoreNotificationEventRepository(context),
                new PurchaseTransactionService(
                    new PurchaseTransactionRepository(context),
                    new PurchaseEventRepository(context)),
                new TransactionalUnitOfWork(context)),
            new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.FromHours(1), batchSize),
            NullLogger<StoreNotificationReconciliationService>.Instance);
        return await service.ReconcileDriftedPurchasesAsync(CancellationToken.None);
    }

    // Like ReconcileSweepAsync, but the purchase read-modify-write for one designated transaction loses an
    // optimistic-concurrency race (CORE-CONC-007): the SQLite test provider carries no xmin token, so the conflict
    // the PostgreSQL token would raise is injected through a decorating repository that mirrors the real
    // PurchaseTransactionRepository.UpdateAsync contract on a lost race (detach the conflicted row from the shared
    // sweep context, then surface a DbUpdateConcurrencyException).
    private async Task<StoreNotificationReconciliationResult> ReconcileSweepWithConflictAsync(
        string conflictTransactionId,
        int batchSize = 50)
    {
        await using var context = CreateContext();
        var purchaseRepository = new ConcurrencyConflictInjectingPurchaseTransactionRepository(
            new PurchaseTransactionRepository(context),
            context,
            conflictTransactionId);
        var service = new StoreNotificationReconciliationService(
            new ReconcilablePurchaseReader(context),
            new StoreNotificationService(
                new StoreNotificationEventRepository(context),
                new PurchaseTransactionService(
                    purchaseRepository,
                    new PurchaseEventRepository(context)),
                new TransactionalUnitOfWork(context)),
            new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.FromHours(1), batchSize),
            NullLogger<StoreNotificationReconciliationService>.Instance);
        return await service.ReconcileDriftedPurchasesAsync(CancellationToken.None);
    }

    private async Task<PurchaseTransaction?> FindTransactionAsync(PurchaseProvider provider, string transactionId)
    {
        await using var context = CreateContext();
        return await new PurchaseTransactionRepository(context)
            .FindByProviderTransactionAsync(provider, transactionId, CancellationToken.None);
    }

    private async Task<int> PurchaseCountAsync()
    {
        await using var context = CreateContext();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private async Task<IReadOnlyList<ReconcilablePurchase>> CandidatesAsync(int batchSize = 50)
    {
        await using var context = CreateContext();
        return await new ReconcilablePurchaseReader(context)
            .ListReconcilablePurchasesAsync(batchSize, CancellationToken.None);
    }

    private static StoreNotification Notification(
        PurchaseProvider provider,
        string notificationId,
        StoreNotificationType type,
        string transactionId,
        DateTimeOffset occurredAt)
        => StoreNotification.Create(provider, notificationId, type, transactionId, occurredAt);

    // --- Out-of-order: the latest-by-event-time notification wins -----------------

    [Fact]
    public async Task An_out_of_order_delivery_is_reconciled_to_the_latest_event()
    {
        // The grace period OCCURRED later (11:00) than the renewal (10:00), but the store delivered the grace period
        // first and the renewal second, so the synchronous webhook applied grace -> renewal and left the purchase
        // ACTIVE — wrong, because the grace period is the latest event. (Both are non-revoked states, which still
        // transition freely; a revoked state would instead be absorbing and never drift — see the monotonic tests.)
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-grace", StoreNotificationType.GracePeriodStarted, "txn-1", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-renew", StoreNotificationType.Renewed, "txn-1", _earlierEvent));

        var beforeReconcile = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(beforeReconcile);
        Assert.Equal(PurchaseTransactionStatus.Active, beforeReconcile.Status); // drifted (wrong) state

        // The reconciliation sweep re-derives the converged status by folding the monotonic machine over the recorded
        // notifications in event-time order (the grace period is the latest event) and converges to InGracePeriod.
        var result = await ReconcileSweepAsync();
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(0, result.Failed);

        var afterReconcile = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(afterReconcile);
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, afterReconcile.Status);
        // The convergence is stamped with the authoritative notification's event time, not the sweep time.
        Assert.Equal(_laterEvent.ToUniversalTime(), afterReconcile.UpdatedAt);
    }

    [Fact]
    public async Task Reconciliation_is_idempotent_a_second_sweep_changes_nothing()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-grace", StoreNotificationType.GracePeriodStarted, "txn-1", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-renew", StoreNotificationType.Renewed, "txn-1", _earlierEvent));

        var first = await ReconcileSweepAsync();
        Assert.Equal(1, first.Reconciled);

        // After convergence the purchase is no longer drifted: it drops out of the candidate set, so a second sweep
        // examines nothing and reconciles nothing (idempotent).
        var second = await ReconcileSweepAsync();
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Reconciled);

        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, transaction.Status);
    }

    // --- Monotonic: a refund stays revoked; reconciliation cannot resurrect it ----

    [Fact]
    public async Task A_refund_followed_by_a_later_renewal_is_not_reconciled_back_to_active()
    {
        // CORE-MON-004: the refund OCCURRED at 10:00 and a legitimate renewal OCCURRED later at 11:00, both for an
        // already-refunded purchase. The webhook already keeps it Refunded (the renewal is a no-op against the
        // absorbing state), so there is NO drift to reconcile — reconciliation cannot resurrect a refund.
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-refund", StoreNotificationType.Refunded, "txn-1", _earlierEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-renew", StoreNotificationType.Renewed, "txn-1", _laterEvent));

        Assert.Equal(PurchaseTransactionStatus.Refunded, (await FindTransactionAsync(PurchaseProvider.Apple, "txn-1"))!.Status);

        // The refunded purchase is terminal, so it is never a reconciliation candidate and the sweep is a no-op.
        Assert.Empty(await CandidatesAsync());
        var result = await ReconcileSweepAsync();
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Reconciled);

        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
    }

    // --- Missed: a notification that arrived before the purchase existed ----------

    [Fact]
    public async Task A_missed_notification_is_reconciled_once_the_purchase_exists()
    {
        // The refund notification arrived BEFORE the purchase was ever recorded, so the webhook recorded it but
        // could apply nothing (TransactionNotFound) — its effect was missed.
        var handled = await HandleAsync(
            Notification(PurchaseProvider.Google, "ntf-early-refund", StoreNotificationType.Refunded, "order-1", _laterEvent));
        Assert.Equal(StoreNotificationProcessingOutcome.TransactionNotFound, handled.Outcome);
        Assert.Null(await FindTransactionAsync(PurchaseProvider.Google, "order-1")); // nothing fabricated

        // Later the purchase is verified and recorded (Active).
        await RecordPurchaseAsync(PurchaseProvider.Google, "order-1");

        // Reconciliation re-derives from the recorded (previously-missed) refund and converges the purchase.
        var result = await ReconcileSweepAsync();
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Reconciled);

        var transaction = await FindTransactionAsync(PurchaseProvider.Google, "order-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
    }

    // --- Duplicate: a webhook-converged purchase needs no reconciliation ----------

    [Fact]
    public async Task A_duplicate_notification_leaves_nothing_to_reconcile()
    {
        // The webhook applied a refund and converged the purchase; a re-delivery of the same notification is
        // deduplicated (AlreadyProcessed) and changes nothing.
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        var notification = Notification(PurchaseProvider.Apple, "ntf-dup", StoreNotificationType.Refunded, "txn-1", _laterEvent);
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, (await HandleAsync(notification)).Outcome);
        Assert.Equal(StoreNotificationProcessingOutcome.AlreadyProcessed, (await HandleAsync(notification)).Outcome);

        // The purchase already matches the latest notification, so reconciliation finds no candidate and is a
        // safe no-op.
        Assert.Empty(await CandidatesAsync());
        var result = await ReconcileSweepAsync();
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Reconciled);

        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
    }

    [Fact]
    public async Task A_purchase_the_webhook_already_converged_is_not_a_candidate()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-cancel", StoreNotificationType.Cancelled, "txn-1", _laterEvent));

        // The webhook already drove the purchase to Cancelled in delivery order, so there is no drift to reconcile.
        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    public async Task A_purchase_with_no_notifications_is_never_a_candidate()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        // No notifications recorded: nothing to reconcile against, so the purchase is never examined.
        Assert.Empty(await CandidatesAsync());
        var result = await ReconcileSweepAsync();
        Assert.Equal(0, result.Examined);
    }

    // --- Fail-closed: never fabricate a purchase ---------------------------------

    [Fact]
    public async Task Reconciliation_never_fabricates_a_purchase_for_a_notification_with_no_transaction()
    {
        // A notification was recorded for a purchase Core never persisted. It is not a reconciliation candidate
        // (the candidate reader joins to existing purchases), and a direct reconcile fails closed.
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-orphan", StoreNotificationType.Refunded, "txn-orphan", _laterEvent));

        Assert.Empty(await CandidatesAsync());
        var result = await ReconcileSweepAsync();
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Reconciled);

        // Nothing is fabricated: no purchase row exists.
        Assert.Equal(0, await PurchaseCountAsync());
        Assert.Null(await FindTransactionAsync(PurchaseProvider.Apple, "txn-orphan"));
    }

    [Fact]
    public async Task Reconcile_transaction_for_an_unknown_purchase_returns_transaction_not_found()
    {
        await HandleAsync(Notification(PurchaseProvider.Apple, "ntf-orphan", StoreNotificationType.Refunded, "txn-orphan", _laterEvent));

        await using var context = CreateContext();
        var service = new StoreNotificationService(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context));

        var outcome = await service.ReconcileTransactionAsync(PurchaseProvider.Apple, "txn-orphan", CancellationToken.None);

        Assert.Equal(StoreNotificationReconciliationOutcome.TransactionNotFound, outcome);
        Assert.Equal(0, await PurchaseCountAsync());
    }

    [Fact]
    public async Task Reconcile_transaction_with_no_notifications_is_a_no_op()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        await using var context = CreateContext();
        var service = new StoreNotificationService(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context));

        var outcome = await service.ReconcileTransactionAsync(PurchaseProvider.Apple, "txn-1", CancellationToken.None);

        Assert.Equal(StoreNotificationReconciliationOutcome.NoNotifications, outcome);
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
    }

    // --- Tenancy-free, multi-purchase sweep -------------------------------------

    [Fact]
    public async Task A_sweep_reconciles_only_drifted_purchases_and_leaves_consistent_ones()
    {
        // Drifted purchase A (out-of-order grace/renewal leaves it Active, should be InGracePeriod).
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-a");
        await HandleAsync(Notification(PurchaseProvider.Apple, "a-grace", StoreNotificationType.GracePeriodStarted, "txn-a", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "a-renew", StoreNotificationType.Renewed, "txn-a", _earlierEvent));

        // Consistent purchase B (webhook converged it to Cancelled in order; a revoked state is terminal, never drifted).
        await RecordPurchaseAsync(PurchaseProvider.Google, "txn-b");
        await HandleAsync(Notification(PurchaseProvider.Google, "b-cancel", StoreNotificationType.Cancelled, "txn-b", _laterEvent));

        var result = await ReconcileSweepAsync();

        // Only A is drifted, so only A is examined and reconciled; B is untouched.
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Reconciled);

        var a = await FindTransactionAsync(PurchaseProvider.Apple, "txn-a");
        Assert.NotNull(a);
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, a.Status);

        var b = await FindTransactionAsync(PurchaseProvider.Google, "txn-b");
        Assert.NotNull(b);
        Assert.Equal(PurchaseTransactionStatus.Cancelled, b.Status);
    }

    // --- Concurrency conflicts in the worker sweep (CORE-CONC-007) ----------------

    [Fact]
    public async Task A_concurrency_conflict_on_one_purchase_is_skipped_and_the_sweep_reconciles_the_rest()
    {
        // Two purchases, both drifted by an out-of-order grace/renewal delivery: the webhook applied grace -> renew
        // and left each Active, but the grace period is the latest event, so each should converge to InGracePeriod.
        // The candidate reader orders by (provider, provider transaction id), so "txn-conflict" is reconciled FIRST
        // and "txn-ok" SECOND on the SAME shared sweep context.
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-conflict");
        await HandleAsync(Notification(PurchaseProvider.Apple, "c-grace", StoreNotificationType.GracePeriodStarted, "txn-conflict", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "c-renew", StoreNotificationType.Renewed, "txn-conflict", _earlierEvent));

        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-ok");
        await HandleAsync(Notification(PurchaseProvider.Apple, "o-grace", StoreNotificationType.GracePeriodStarted, "txn-ok", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "o-renew", StoreNotificationType.Renewed, "txn-ok", _earlierEvent));

        // The reconciliation read-modify-write on "txn-conflict" loses an optimistic-concurrency race. The worker has
        // no ConcurrencyConflictMiddleware, so the loop itself must catch the DbUpdateConcurrencyException, skip the
        // conflicted purchase and carry on — without crashing and without poisoning the rest of the batch.
        var result = await ReconcileSweepWithConflictAsync("txn-conflict");

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Reconciled);
        Assert.Equal(1, result.Failed);

        // The conflicted purchase committed no status change — it stays drifted (Active) for the next sweep to retry.
        var conflicted = await FindTransactionAsync(PurchaseProvider.Apple, "txn-conflict");
        Assert.NotNull(conflicted);
        Assert.Equal(PurchaseTransactionStatus.Active, conflicted.Status);

        // The other purchase was still reconciled on the same shared context AFTER the conflict — proof the conflict
        // neither tore the loop down nor poisoned the subsequent purchase's SaveChanges.
        var ok = await FindTransactionAsync(PurchaseProvider.Apple, "txn-ok");
        Assert.NotNull(ok);
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, ok.Status);
    }

    [Fact]
    public async Task A_purchase_that_conflicted_is_reconciled_on_a_later_sweep()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-conflict");
        await HandleAsync(Notification(PurchaseProvider.Apple, "c-grace", StoreNotificationType.GracePeriodStarted, "txn-conflict", _laterEvent));
        await HandleAsync(Notification(PurchaseProvider.Apple, "c-renew", StoreNotificationType.Renewed, "txn-conflict", _earlierEvent));

        // First sweep: the convergence loses the race, so it is skipped and the purchase is left drifted.
        var first = await ReconcileSweepWithConflictAsync("txn-conflict");
        Assert.Equal(1, first.Examined);
        Assert.Equal(0, first.Reconciled);
        Assert.Equal(1, first.Failed);
        Assert.Equal(
            PurchaseTransactionStatus.Active,
            (await FindTransactionAsync(PurchaseProvider.Apple, "txn-conflict"))!.Status);

        // The next sweep (no injected conflict) retries the still-drifted purchase and converges it — the conflict
        // was a transient skip, not a permanent failure.
        var second = await ReconcileSweepAsync();
        Assert.Equal(1, second.Examined);
        Assert.Equal(1, second.Reconciled);
        Assert.Equal(0, second.Failed);
        Assert.Equal(
            PurchaseTransactionStatus.InGracePeriod,
            (await FindTransactionAsync(PurchaseProvider.Apple, "txn-conflict"))!.Status);
    }

    /// <summary>
    /// A decorating <see cref="IPurchaseTransactionRepository"/> that makes the read-modify-write for ONE designated
    /// purchase lose an optimistic-concurrency race, leaving every other call to the real repository untouched. The
    /// default in-memory SQLite test provider carries no <c>xmin</c> token (the token is mapped only on Npgsql — see
    /// <see cref="LiveCoreDbContext"/>), so the conflict a concurrent writer would trigger on PostgreSQL is simulated
    /// here. It faithfully reproduces the real <see cref="PurchaseTransactionRepository.UpdateAsync"/> contract on a
    /// lost race (CORE-CONC-007): it DETACHES the conflicted, still-Modified entity from the shared sweep context so
    /// it is not re-sent by a later SaveChanges, then throws a <see cref="DbUpdateConcurrencyException"/>. The real
    /// repository's detach-and-rethrow is verified directly by the repository tests.
    /// </summary>
    private sealed class ConcurrencyConflictInjectingPurchaseTransactionRepository : IPurchaseTransactionRepository
    {
        private readonly IPurchaseTransactionRepository _inner;
        private readonly LiveCoreDbContext _context;
        private readonly string _conflictTransactionId;

        public ConcurrencyConflictInjectingPurchaseTransactionRepository(
            IPurchaseTransactionRepository inner,
            LiveCoreDbContext context,
            string conflictTransactionId)
        {
            _inner = inner;
            _context = context;
            _conflictTransactionId = conflictTransactionId;
        }

        public Task<PurchaseTransactionAddResult> AddAsync(
            PurchaseTransaction transaction,
            CancellationToken cancellationToken)
            => _inner.AddAsync(transaction, cancellationToken);

        public Task<PurchaseTransaction?> FindByProviderTransactionAsync(
            PurchaseProvider provider,
            string providerTransactionId,
            CancellationToken cancellationToken)
            => _inner.FindByProviderTransactionAsync(provider, providerTransactionId, cancellationToken);

        public Task UpdateAsync(PurchaseTransaction transaction, CancellationToken cancellationToken)
        {
            if (string.Equals(transaction.ProviderTransactionId, _conflictTransactionId, StringComparison.Ordinal))
            {
                // Mirror the real repository's post-conflict contract: keep the shared context usable by detaching the
                // abandoned change, then surface the conflict for the loop to handle.
                _context.Entry(transaction).State = EntityState.Detached;
                throw new DbUpdateConcurrencyException(
                    "Simulated optimistic-concurrency conflict on the purchase read-modify-write.");
            }

            return _inner.UpdateAsync(transaction, cancellationToken);
        }
    }
}
