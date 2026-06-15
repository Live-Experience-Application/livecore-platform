using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Failure-injection tests for the ATOMIC ROLLBACK of <see cref="StoreNotificationService.HandleAsync"/> when the
/// entitlement REVOKE step fails (CORE-TST-008, the test-enforcement story closing the gap the adversarial review
/// found: the atomic-rollback claim for the revoke + status change + ledger trio — StoreNotificationService.cs
/// ~180-199 — was asserted in prose but had no test). They mirror the CORE-CONC
/// <c>TransactionalCommandAtomicityTests</c> pattern: swap ONE production seam for a throwing one and assert that a
/// part-way failure rolls EVERYTHING back, leaving nothing half-applied.
///
/// On a revoking notification (a refund) <see cref="StoreNotificationService.HandleAsync"/> revokes the granted
/// entitlement BEFORE the purchase status change and the dedup-ledger write, all inside ONE
/// <see cref="TransactionalUnitOfWork"/> (CORE-MON-010 / CORE-MON-004). The seam swapped here is the
/// <see cref="ISubjectEntitlementRepository"/> the revoke writes through (the only writer of subject entitlements in
/// the handler's graph): a decorator throws on the revoke's <c>UpdateAsync</c>, so the failure lands squarely in the
/// revoke step. The tests pin that "status change + ledger + revoke are all-or-nothing":
/// <list type="bullet">
///   <item>a revoke-step failure leaves the purchase still <see cref="PurchaseTransactionStatus.Active"/> (no
///   downgrade), writes NO dedup-ledger row, appends NO downgrade purchase event, and leaves the buyer's
///   entitlements intact (the revoke rolled back); and</item>
///   <item>even a revoke that has ALREADY persisted part of its work — the first of the plan's two entitlements
///   revoked inside the transaction — is rolled back when a later revoke write in the same step fails, so a
///   re-delivery replays the whole effect from scratch rather than finding a half-revoked subject.</item>
/// </list>
/// The service is wired with its real revocation collaborator and the full CORE-MON-002/003 grant chain over an
/// in-memory SQLite database (the same harness as the other Store data-layer tests). All fixtures are generic Core
/// vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class StoreNotificationRevokeFailureRollbackTests : IDisposable
{
    private const string _productReference = "product.premium";
    private const string _adFreeKey = "ads.disabled";
    private const string _prioritySupportKey = "support.priority";

    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _refundEvent = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _receivedAt = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public StoreNotificationRevokeFailureRollbackTests()
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

    // The full webhook graph WITH its revocation collaborator, exactly as the API/worker hosts wire it — except the
    // subject-entitlement repository the revoke writes through is supplied by the caller, so a test can swap in a
    // decorator that fails the revoke write. Built per call over a fresh context to model a request scope, so every
    // repository the handler composes shares the one context the unit of work begins its transaction on.
    private static StoreNotificationService NotificationService(
        LiveCoreDbContext context,
        ISubjectEntitlementRepository subjectEntitlements)
        => new(
            new StoreNotificationEventRepository(context),
            new PurchaseTransactionService(
                new PurchaseTransactionRepository(context),
                new PurchaseEventRepository(context)),
            new TransactionalUnitOfWork(context),
            new PurchaseEntitlementRevocationService(
                new PurchaseTransactionRepository(context),
                new BillingAccountLinkRepository(context),
                new ProductEntitlementGrantService(
                    new PlanDefinitionRepository(context),
                    new SubjectEntitlementAssignmentService(
                        subjectEntitlements,
                        new PlanDefinitionRepository(context),
                        new EntitlementDefinitionRepository(context)))));

    // Seeds the plan (granting TWO flag entitlements), records the purchase, links the buyer and grants both
    // entitlements — the verified, buyer-linked, granted state a purchase is in before a refund (CORE-MON-002/003).
    // Two entitlements let a later test prove a PARTIALLY-persisted revoke rolls back (the first revoke write
    // commits inside the transaction, the second fails).
    private async Task<Guid> SeedGrantedPurchaseAsync(PurchaseProvider provider, string transactionId)
    {
        var subjectId = Guid.CreateVersion7();

        await using (var context = CreateContext())
        {
            var adFree = EntitlementDefinition.Define(_adFreeKey, EntitlementValueKind.Flag, "Ad free", null, _recordedAt);
            var prioritySupport = EntitlementDefinition.Define(
                _prioritySupportKey, EntitlementValueKind.Flag, "Priority support", null, _recordedAt);
            context.EntitlementDefinitions.Add(adFree);
            context.EntitlementDefinitions.Add(prioritySupport);
            await context.SaveChangesAsync();

            // Plan keyed by the product reference, granting both flags.
            var plan = PlanDefinition.Define(_productReference, "Premium", null, _recordedAt);
            plan.GrantFlag(adFree, true);
            plan.GrantFlag(prioritySupport, true);
            Assert.Equal(PlanDefinitionAddResult.Added, await new PlanDefinitionRepository(context).AddAsync(plan, CancellationToken.None));

            // Record the verified purchase, link the buyer and grant the mapped entitlements (CORE-MON-002/003).
            var recording = await new PurchaseTransactionService(
                    new PurchaseTransactionRepository(context),
                    new PurchaseEventRepository(context))
                .RecordVerifiedPurchaseAsync(
                    VerifiedPurchase.Create(provider, transactionId, _productReference), _recordedAt, CancellationToken.None);

            await new BillingAccountLinkService(new BillingAccountLinkRepository(context))
                .LinkBuyerAsync(recording.Transaction.Id, EntitlementSubjectType.User, subjectId, _recordedAt, CancellationToken.None);

            await new ProductEntitlementGrantService(
                    new PlanDefinitionRepository(context),
                    new SubjectEntitlementAssignmentService(
                        new SubjectEntitlementRepository(context),
                        new PlanDefinitionRepository(context),
                        new EntitlementDefinitionRepository(context)))
                .GrantForProductAsync(EntitlementSubjectType.User, subjectId, _productReference, _recordedAt, CancellationToken.None);
        }

        // Precondition: the buyer holds BOTH granted entitlements before any refund.
        var granted = await ResolveAsync(subjectId);
        Assert.Equal(2, granted.Count);
        Assert.True(granted.IsFlagEnabled(_adFreeKey));
        Assert.True(granted.IsFlagEnabled(_prioritySupportKey));
        return subjectId;
    }

    // Handles the notification through the service wired with a revoke-write that throws on the Nth UpdateAsync,
    // asserting the injected failure surfaces (the unit of work never commits).
    private async Task HandleExpectingRevokeFailureAsync(StoreNotification notification, int failOnUpdateNumber)
    {
        await using var context = CreateContext();
        var service = NotificationService(
            context,
            new RevokeWriteFailingSubjectEntitlementRepository(new SubjectEntitlementRepository(context), failOnUpdateNumber));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.HandleAsync(notification, _receivedAt, CancellationToken.None));
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

    private async Task<int> LedgerCountAsync()
    {
        await using var context = CreateContext();
        return await context.StoreNotificationEvents.AsNoTracking().CountAsync();
    }

    private async Task<int> PurchaseEventCountAsync()
    {
        await using var context = CreateContext();
        return await context.PurchaseEvents.AsNoTracking().CountAsync();
    }

    private static StoreNotification RefundNotification(PurchaseProvider provider, string transactionId)
        => StoreNotification.Create(provider, "ntf-refund", StoreNotificationType.Refunded, transactionId, _refundEvent);

    // --- The required test: a revoke-step failure rolls the whole unit of work back ---

    [Fact]
    public async Task A_revoke_step_failure_rolls_back_the_status_change_and_the_ledger_write()
    {
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        // The refund revokes the granted entitlement BEFORE the status change and the ledger write; injecting a
        // failure on the very first revoke write fails the revoke step inside the one unit of work.
        await HandleExpectingRevokeFailureAsync(RefundNotification(PurchaseProvider.Apple, "txn-1"), failOnUpdateNumber: 1);

        // Nothing persisted (status change + ledger + revoke are all-or-nothing): the purchase is still Active (the
        // downgrade rolled back), no dedup-ledger row exists, and the purchase trail carries only the initial
        // recording event (no downgrade purchase event was committed).
        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(0, await LedgerCountAsync());
        Assert.Equal(1, await PurchaseEventCountAsync());

        // ...and the buyer still holds BOTH entitlements: the revoke rolled back too.
        var entitlements = await ResolveAsync(subjectId);
        Assert.Equal(2, entitlements.Count);
        Assert.True(entitlements.IsFlagEnabled(_adFreeKey));
        Assert.True(entitlements.IsFlagEnabled(_prioritySupportKey));
    }

    // --- A revoke that already persisted part of its work is rolled back too ---

    [Fact]
    public async Task A_partially_persisted_revoke_is_rolled_back_when_a_later_revoke_write_fails()
    {
        var subjectId = await SeedGrantedPurchaseAsync(PurchaseProvider.Apple, "txn-1");

        // The refund revokes BOTH of the plan's entitlements; the FIRST revoke write persists inside the unit of
        // work's transaction, then the SECOND write fails. The whole unit of work must roll back — including the
        // already-persisted first revoke — so the effect is genuinely all-or-nothing.
        await HandleExpectingRevokeFailureAsync(RefundNotification(PurchaseProvider.Apple, "txn-1"), failOnUpdateNumber: 2);

        Assert.Equal(PurchaseTransactionStatus.Active, await StatusAsync(PurchaseProvider.Apple, "txn-1"));
        Assert.Equal(0, await LedgerCountAsync());
        Assert.Equal(1, await PurchaseEventCountAsync());

        // BOTH entitlements are restored: the first revoke that already wrote inside the transaction was rolled
        // back with the rest, so a re-delivery replays the whole effect from scratch rather than seeing a
        // half-revoked subject.
        var entitlements = await ResolveAsync(subjectId);
        Assert.Equal(2, entitlements.Count);
        Assert.True(entitlements.IsFlagEnabled(_adFreeKey));
        Assert.True(entitlements.IsFlagEnabled(_prioritySupportKey));
    }

    /// <summary>
    /// An <see cref="ISubjectEntitlementRepository"/> decorator that serves every read and add from the real
    /// repository but THROWS on the Nth <c>UpdateAsync</c> — the write the entitlement REVOKE persists through
    /// (<see cref="SubjectEntitlementAssignmentService.RevokeAsync"/>). It models a failure inside the revoke step
    /// of <see cref="StoreNotificationService.HandleAsync"/>: failing on the first write fails before any revoke
    /// persists, while failing on a later write fails AFTER an earlier revoke has already committed inside the
    /// transaction — both must roll the whole unit of work back.
    /// </summary>
    private sealed class RevokeWriteFailingSubjectEntitlementRepository : ISubjectEntitlementRepository
    {
        private readonly ISubjectEntitlementRepository _inner;
        private readonly int _failOnUpdateNumber;
        private int _updateCount;

        public RevokeWriteFailingSubjectEntitlementRepository(ISubjectEntitlementRepository inner, int failOnUpdateNumber)
        {
            _inner = inner;
            _failOnUpdateNumber = failOnUpdateNumber;
        }

        public Task UpdateAsync(SubjectEntitlement assignment, CancellationToken cancellationToken)
        {
            _updateCount++;
            return _updateCount >= _failOnUpdateNumber
                ? throw new InvalidOperationException("Injected revoke-step write failure.")
                : _inner.UpdateAsync(assignment, cancellationToken);
        }

        public Task<SubjectEntitlementAddResult> AddAsync(SubjectEntitlement assignment, CancellationToken cancellationToken)
            => _inner.AddAsync(assignment, cancellationToken);

        public Task<SubjectEntitlement?> FindBySubjectAndEntitlementAsync(
            EntitlementSubjectType subjectType,
            Guid subjectId,
            Guid entitlementDefinitionId,
            CancellationToken cancellationToken)
            => _inner.FindBySubjectAndEntitlementAsync(subjectType, subjectId, entitlementDefinitionId, cancellationToken);

        public Task<IReadOnlyList<SubjectEntitlement>> ListBySubjectAsync(
            EntitlementSubjectType subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
            => _inner.ListBySubjectAsync(subjectType, subjectId, cancellationToken);

        public Task<IReadOnlyList<SubjectEntitlement>> ListActiveBySubjectAsync(
            EntitlementSubjectType subjectType,
            Guid subjectId,
            CancellationToken cancellationToken)
            => _inner.ListActiveBySubjectAsync(subjectType, subjectId, cancellationToken);
    }
}
