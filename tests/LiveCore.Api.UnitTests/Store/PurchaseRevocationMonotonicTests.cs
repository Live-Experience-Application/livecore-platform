using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the MONOTONIC purchase-status + entitlement-revocation behavior (CORE-MON-004) — the
/// story's required test: "A Refunded purchase followed by a later-OccurredAt Renewed notification stays Refunded
/// and the entitlement stays revoked; reconciliation cannot resurrect it." They run the real
/// <see cref="StoreNotificationService"/> WITH its <see cref="PurchaseEntitlementRevocationService"/> collaborator
/// over the real repositories on an in-memory SQLite database (the same harness as the other Store data-layer
/// tests), wiring the full grant chain (CORE-MON-002 buyer link + CORE-MON-003 product → plan → entitlement grant)
/// so the entitlement effect of a refund is observable end to end through the same effective-entitlements read the
/// GET /api/v1/me/entitlements endpoint uses.
///
/// The tests pin both halves of the acceptance criterion:
/// <list type="bullet">
///   <item>a refund notification REVOKES the granted entitlement (the purchase becomes Refunded and the buyer's
///   premium state disappears);</item>
///   <item>a later-OccurredAt renewal notification leaves the purchase Refunded and the entitlement revoked (the
///   revoked state is terminal — a renewal cannot resurrect it), through the synchronous webhook AND through the
///   reconciliation sweep (a missed refund the webhook could not apply is converged and revoked).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class PurchaseRevocationMonotonicTests : IDisposable
{
    private const string _productReference = "product.premium";
    private const string _adFreeKey = "ads.disabled";

    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _refundEvent = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _renewEvent = new(2026, 6, 12, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _receivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PurchaseRevocationMonotonicTests()
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

    // The full webhook graph: the notification service WITH its revocation collaborator, exactly as the API/worker
    // hosts wire it. Built per call over a fresh context to model a request/sweep scope.
    private StoreNotificationService NotificationService(LiveCoreDbContext context)
        => new(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context),
            new PurchaseEntitlementRevocationService(
                new PurchaseTransactionRepository(context),
                new BillingAccountLinkRepository(context),
                ProductGrants(context)));

    private static ProductEntitlementGrantService ProductGrants(LiveCoreDbContext context)
        => new(
            new PlanDefinitionRepository(context),
            new SubjectEntitlementAssignmentService(
                new SubjectEntitlementRepository(context),
                new PlanDefinitionRepository(context),
                new EntitlementDefinitionRepository(context)));

    // Seeds the plan + entitlement, records the purchase, links the buyer and grants the entitlement — the verified,
    // buyer-linked, granted state a purchase is in before a refund (the CORE-MON-002/003 chain).
    private async Task<Guid> SeedGrantedPurchaseAsync(PurchaseProvider provider, string transactionId)
    {
        var subjectId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            // Plan keyed by the product reference, granting the ad-free flag.
            var adFree = EntitlementDefinition.Define(_adFreeKey, EntitlementValueKind.Flag, "Ad free", null, _recordedAt);
            context.EntitlementDefinitions.Add(adFree);
            await context.SaveChangesAsync();

            var plan = PlanDefinition.Define(_productReference, "Premium", null, _recordedAt);
            plan.GrantFlag(adFree, true);
            Assert.Equal(PlanDefinitionAddResult.Added, await new PlanDefinitionRepository(context).AddAsync(plan, CancellationToken.None));

            // Record the verified purchase, link the buyer and grant the mapped entitlement (CORE-MON-002/003).
            var recording = await new PurchaseTransactionService(
                    new PurchaseTransactionRepository(context),
                    new PurchaseEventRepository(context))
                .RecordVerifiedPurchaseAsync(
                    VerifiedPurchase.Create(provider, transactionId, _productReference), _recordedAt, CancellationToken.None);

            await new BillingAccountLinkService(new BillingAccountLinkRepository(context))
                .LinkBuyerAsync(recording.Transaction.Id, EntitlementSubjectType.User, subjectId, _recordedAt, CancellationToken.None);

            await ProductGrants(context).GrantForProductAsync(
                EntitlementSubjectType.User, subjectId, _productReference, _recordedAt, CancellationToken.None);
        }

        // The buyer holds the granted entitlement before any refund.
        Assert.True((await ResolveAsync(subjectId)).IsFlagEnabled(_adFreeKey));
        return subjectId;
    }

    private async Task<StoreNotificationProcessingResult> HandleAsync(StoreNotification notification)
    {
        await using var context = CreateContext();
        return await NotificationService(context).HandleAsync(notification, _receivedAt, CancellationToken.None);
    }

    private async Task<StoreNotificationReconciliationResult> ReconcileSweepAsync()
    {
        await using var context = CreateContext();
        var service = new StoreNotificationReconciliationService(
            new ReconcilablePurchaseReader(context),
            NotificationService(context),
            new StoreNotificationReconciliationOptions(enabled: true, TimeSpan.FromHours(1), batchSize: 50),
            NullLogger<StoreNotificationReconciliationService>.Instance);
        return await service.ReconcileDriftedPurchasesAsync(CancellationToken.None);
    }

    private async Task<EffectiveEntitlements> ResolveAsync(Guid subjectId)
    {
        await using var context = CreateContext();
        return await new SubjectEntitlementResolver(new SubjectEntitlementRepository(context))
            .ResolveAsync(EntitlementSubjectType.User, subjectId, CancellationToken.None);
    }

    private async Task<PurchaseTransactionStatus> StatusAsync(PurchaseProvider provider, string transactionId)
    {
        await using var context = CreateContext();
        var transaction = await new PurchaseTransactionRepository(context)
            .FindByProviderTransactionAsync(provider, transactionId, CancellationToken.None);
        Assert.NotNull(transaction);
        return transaction!.Status;
    }

    private static StoreNotification Notification(
        string notificationId, StoreNotificationType type, string transactionId, DateTimeOffset occurredAt)
        => StoreNotification.Create(PurchaseProvider.Apple, notificationId, type, transactionId, occurredAt);

    // --- Refund revokes the granted entitlement ---------------------------------

    [Fact]
    public async Task A_refund_notification_revokes_the_granted_entitlement()
    {
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        var result = await HandleAsync(Notification("ntf-refund", StoreNotificationType.Refunded, "txn-1", _refundEvent));

        Assert.Equal(StoreNotificationProcessingOutcome.Applied, result.Outcome);
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        // The buyer's premium state is gone: the entitlement no longer resolves.
        Assert.Equal(0, (await ResolveAsync(subjectId)).Count);
    }

    // --- The required test: a later renewal cannot resurrect the refund ----------

    [Fact]
    public async Task A_later_renewal_after_a_refund_keeps_it_refunded_and_the_entitlement_revoked()
    {
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        // The refund revokes premium...
        await HandleAsync(Notification("ntf-refund", StoreNotificationType.Refunded, "txn-1", _refundEvent));
        // ...and a legitimate renewal delivered afterwards, with a LATER event time, must not resurrect it.
        var renew = await HandleAsync(Notification("ntf-renew", StoreNotificationType.Renewed, "txn-1", _renewEvent));

        Assert.Equal(StoreNotificationProcessingOutcome.Unchanged, renew.Outcome); // the renewal applied nothing
        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(0, (await ResolveAsync(subjectId)).Count); // the entitlement stays revoked
    }

    [Fact]
    public async Task Reconciliation_cannot_resurrect_a_refunded_purchase()
    {
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        await HandleAsync(Notification("ntf-refund", StoreNotificationType.Refunded, "txn-1", _refundEvent));
        await HandleAsync(Notification("ntf-renew", StoreNotificationType.Renewed, "txn-1", _renewEvent));

        // The refunded purchase is terminal, so the sweep finds no drift and changes nothing — it cannot resurrect
        // the refund even though a later renewal is recorded.
        var result = await ReconcileSweepAsync();
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Reconciled);

        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(0, (await ResolveAsync(subjectId)).Count);
    }

    // --- A missed refund (recorded before the purchase) is revoked by reconciliation --

    [Fact]
    public async Task A_missed_refund_is_revoked_when_reconciliation_converges_the_purchase()
    {
        // The refund notification arrives BEFORE the purchase is recorded, so the webhook records it but applies
        // nothing (TransactionNotFound) and there is no entitlement to revoke yet.
        var early = await HandleAsync(Notification("ntf-refund", StoreNotificationType.Refunded, "txn-1", _refundEvent));
        Assert.Equal(StoreNotificationProcessingOutcome.TransactionNotFound, early.Outcome);

        // The purchase is later verified, linked and granted — the buyer now (transiently) holds premium.
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        // Reconciliation re-derives Refunded from the recorded (previously-missed) refund, converges the purchase
        // AND revokes the granted entitlement — the missed-refund revoke path only the reconciliation job can apply.
        var result = await ReconcileSweepAsync();
        Assert.Equal(1, result.Examined);
        Assert.Equal(1, result.Reconciled);

        Assert.Equal(PurchaseTransactionStatus.Refunded, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(0, (await ResolveAsync(subjectId)).Count);
    }
}
