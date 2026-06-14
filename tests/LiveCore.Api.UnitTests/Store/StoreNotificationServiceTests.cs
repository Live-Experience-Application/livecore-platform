using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for <see cref="StoreNotificationService"/> (CORE-STORE-005) — the headline
/// IDEMPOTENCY and DOWNGRADE behavior of the idempotent store notification handling story (the story's required
/// tests: "Duplicate notification and downgrade tests"). The service deduplicates a validated, normalized store
/// notification and drives the affected purchase's lifecycle, reusing the CORE-STORE-002 purchase status change
/// (so the change is itself audited and idempotent).
///
/// The tests run the service over the real repositories on an in-memory SQLite database (mirroring
/// <c>PurchaseTransactionServiceTests</c>). They pin the acceptance criterion "Renewals, cancellations, refunds
/// and grace periods update entitlements safely":
/// <list type="bullet">
///   <item>a refund/cancellation notification DOWNGRADES the purchase (Active → Refunded/Cancelled) and audits it
///   on both the purchase trail and the notification ledger;</item>
///   <item>a DUPLICATE notification (same provider + notification id) is an idempotent no-op — no second status
///   change, no second ledger row, no second audit event ("Store notifications must be idempotent", docs/21);</item>
///   <item>a renewal keeps/reactivates Active and a grace period moves to the explicit InGracePeriod state;</item>
///   <item>a notification for an unknown purchase FAILS CLOSED (nothing fabricated) yet is still recorded so it is
///   auditable and not reprocessed.</item>
/// </list>
/// All fixtures are generic Store vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class StoreNotificationServiceTests : IDisposable
{
    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _occurredAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _receivedAt = new(2026, 6, 12, 10, 0, 5, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public StoreNotificationServiceTests()
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

    // Each call models its own request scope: a fresh context with the module repositories over it.
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
                new PurchaseEventRepository(context)));
        return await service.HandleAsync(notification, _receivedAt, CancellationToken.None);
    }

    private async Task<PurchaseTransaction?> FindTransactionAsync(PurchaseProvider provider, string transactionId)
    {
        await using var context = CreateContext();
        return await new PurchaseTransactionRepository(context)
            .FindByProviderTransactionAsync(provider, transactionId, CancellationToken.None);
    }

    private async Task<int> NotificationCountAsync()
    {
        await using var context = CreateContext();
        return await context.StoreNotificationEvents.AsNoTracking().CountAsync();
    }

    private async Task<IReadOnlyList<StoreNotificationEvent>> NotificationsAsync()
    {
        await using var context = CreateContext();
        return await context.StoreNotificationEvents.AsNoTracking()
            .OrderBy(notification => notification.Id)
            .ToListAsync();
    }

    private async Task<int> PurchaseEventCountAsync()
    {
        await using var context = CreateContext();
        return await context.PurchaseEvents.AsNoTracking().CountAsync();
    }

    private static StoreNotification Notification(
        PurchaseProvider provider = PurchaseProvider.Apple,
        string notificationId = "ntf-1",
        StoreNotificationType type = StoreNotificationType.Refunded,
        string transactionId = "txn-1")
        => StoreNotification.Create(provider, notificationId, type, transactionId, _occurredAt);

    // --- Downgrade: a refund revokes premium and audits it ----------------------

    [Fact]
    public async Task A_refund_notification_downgrades_an_active_purchase_and_audits_it()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        var result = await HandleAsync(Notification(type: StoreNotificationType.Refunded, transactionId: "txn-1"));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, result.Outcome);

        // The purchase is downgraded to Refunded (the server-side source of truth for premium state).
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);

        // The notification is recorded as an auditable ledger fact (applied, drove the purchase to Refunded).
        var ledgerRow = Assert.Single(await NotificationsAsync());
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, ledgerRow.Outcome);
        Assert.Equal(PurchaseTransactionStatus.Refunded, ledgerRow.AppliedStatus);
        Assert.Equal(StoreNotificationType.Refunded, ledgerRow.NotificationType);

        // The purchase-side change is audited too: the initial recording plus the Active -> Refunded transition.
        Assert.Equal(2, await PurchaseEventCountAsync());
    }

    [Fact]
    public async Task A_handled_notification_records_the_stores_event_time_on_the_ledger()
    {
        // The ledger row carries the store's reported event time (occurred_at), distinct from received_at — the
        // ordering key the reconciliation job (CORE-JOB-003) re-derives a purchase's converged status from.
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        await HandleAsync(Notification(type: StoreNotificationType.Refunded, transactionId: "txn-1"));

        var ledgerRow = Assert.Single(await NotificationsAsync());
        Assert.Equal(_occurredAt.ToUniversalTime(), ledgerRow.OccurredAt);
        Assert.Equal(_receivedAt.ToUniversalTime(), ledgerRow.ReceivedAt);
    }

    [Fact]
    public async Task A_cancellation_notification_downgrades_to_cancelled()
    {
        await RecordPurchaseAsync(PurchaseProvider.Google, "order-1");

        var result = await HandleAsync(Notification(
            PurchaseProvider.Google, "ntf-cancel", StoreNotificationType.Cancelled, "order-1"));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, result.Outcome);
        var transaction = await FindTransactionAsync(PurchaseProvider.Google, "order-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Cancelled, transaction.Status);
    }

    [Fact]
    public async Task A_grace_period_notification_moves_to_the_explicit_in_grace_period_state()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-grace");

        var result = await HandleAsync(Notification(
            type: StoreNotificationType.GracePeriodStarted, transactionId: "txn-grace"));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, result.Outcome);
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-grace");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.InGracePeriod, transaction.Status);
    }

    // --- Idempotency: a duplicate notification changes nothing twice ------------

    [Fact]
    public async Task A_duplicate_notification_is_an_idempotent_no_op()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        var notification = Notification(notificationId: "ntf-dup", type: StoreNotificationType.Refunded, transactionId: "txn-1");

        var first = await HandleAsync(notification);
        var second = await HandleAsync(notification);

        // First applies the downgrade; the exact-same notification (same provider + notification id) is recognized
        // by the dedup ledger and ignored — no second status change, no second ledger row, no second audit event.
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, first.Outcome);
        Assert.Equal(StoreNotificationProcessingOutcome.AlreadyProcessed, second.Outcome);
        Assert.Equal(1, await NotificationCountAsync());
        Assert.Equal(2, await PurchaseEventCountAsync()); // recording + one downgrade only

        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);
    }

    // --- Renewals: keep or reactivate Active ------------------------------------

    [Fact]
    public async Task A_renewal_notification_for_an_already_active_purchase_is_recorded_but_changes_nothing()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        var result = await HandleAsync(Notification(
            notificationId: "ntf-renew", type: StoreNotificationType.Renewed, transactionId: "txn-1"));

        // The purchase is already Active, so the status change is a no-op (no purchase event) — but the
        // notification's arrival is still recorded as a ledger fact (outcome Unchanged).
        Assert.Equal(StoreNotificationProcessingOutcome.Unchanged, result.Outcome);
        Assert.Equal(1, await PurchaseEventCountAsync()); // only the initial recording
        var ledgerRow = Assert.Single(await NotificationsAsync());
        Assert.Equal(StoreNotificationProcessingOutcome.Unchanged, ledgerRow.Outcome);
    }

    [Fact]
    public async Task A_renewal_notification_reactivates_a_purchase_that_was_in_a_grace_period()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(notificationId: "ntf-grace", type: StoreNotificationType.GracePeriodStarted, transactionId: "txn-1"));

        var result = await HandleAsync(Notification(
            notificationId: "ntf-renew", type: StoreNotificationType.Renewed, transactionId: "txn-1"));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, result.Outcome);
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Active, transaction.Status);
    }

    // --- Monotonic: a refund is terminal; a later renewal cannot resurrect it ----

    [Fact]
    public async Task A_renewal_after_a_refund_keeps_the_purchase_refunded()
    {
        // CORE-MON-004: once a purchase is Refunded the state is absorbing, so a renewal notification delivered
        // afterwards (a legitimate DID_RENEW with a later event time) is recorded but cannot flip it back to Active.
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");
        await HandleAsync(Notification(notificationId: "ntf-refund", type: StoreNotificationType.Refunded, transactionId: "txn-1"));

        var renew = await HandleAsync(StoreNotification.Create(
            PurchaseProvider.Apple, "ntf-renew", StoreNotificationType.Renewed, "txn-1", _occurredAt.AddHours(1)));

        // The renewal is a recorded no-op: the purchase stays Refunded (revoked stays revoked).
        Assert.Equal(StoreNotificationProcessingOutcome.Unchanged, renew.Outcome);
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);

        // No second purchase-side transition was audited (recording + the one refund only); the renewal is recorded
        // on the notification ledger but applied nothing.
        Assert.Equal(2, await PurchaseEventCountAsync());
        Assert.Equal(2, await NotificationCountAsync());
    }

    // --- Fail-closed: an unknown purchase is never fabricated -------------------

    [Fact]
    public async Task A_notification_for_an_unknown_purchase_fails_closed_but_is_recorded()
    {
        // No purchase recorded for txn-missing.
        var result = await HandleAsync(Notification(
            notificationId: "ntf-missing", type: StoreNotificationType.Refunded, transactionId: "txn-missing"));

        Assert.Equal(StoreNotificationProcessingOutcome.TransactionNotFound, result.Outcome);
        // Nothing is fabricated: no purchase row exists.
        Assert.Null(await FindTransactionAsync(PurchaseProvider.Apple, "txn-missing"));
        // But the notification's arrival is recorded so it is auditable and not reprocessed.
        var ledgerRow = Assert.Single(await NotificationsAsync());
        Assert.Equal(StoreNotificationProcessingOutcome.TransactionNotFound, ledgerRow.Outcome);
    }

    // --- Distinct notifications for one purchase each apply ----------------------

    [Fact]
    public async Task Distinct_notifications_for_the_same_purchase_each_apply_and_are_each_recorded()
    {
        await RecordPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        var grace = await HandleAsync(Notification(notificationId: "ntf-1", type: StoreNotificationType.GracePeriodStarted, transactionId: "txn-1"));
        var refund = await HandleAsync(Notification(notificationId: "ntf-2", type: StoreNotificationType.Refunded, transactionId: "txn-1"));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, grace.Outcome);
        Assert.Equal(StoreNotificationProcessingOutcome.Applied, refund.Outcome);

        // Two distinct notifications -> two ledger rows; the purchase ends Refunded.
        Assert.Equal(2, await NotificationCountAsync());
        var transaction = await FindTransactionAsync(PurchaseProvider.Apple, "txn-1");
        Assert.NotNull(transaction);
        Assert.Equal(PurchaseTransactionStatus.Refunded, transaction.Status);

        // The purchase trail records the recording plus both transitions.
        Assert.Equal(3, await PurchaseEventCountAsync());
    }
}
