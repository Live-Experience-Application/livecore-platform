using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for <see cref="PurchaseTransactionService"/> (CORE-STORE-002) — the headline
/// IDEMPOTENCY and AUDIT behavior of the purchase transaction persistence and audit trail story. The service
/// records a verified purchase and audits every purchase state change.
///
/// The tests run the service over the real repositories on an in-memory SQLite database. They pin the acceptance
/// criterion "Purchase state changes are persisted and auditable": recording a verified purchase persists one
/// transaction and writes one initial audit event; recording the SAME purchase again is an idempotent no-op
/// (still one transaction, still one event — "Store notifications must be idempotent", docs/21); a status change
/// is audited with its before/after pair, while a no-op change writes no event; a change to an unknown purchase
/// fails closed; and a purchase's full lifecycle is reconstructable from its append-only event trail. All
/// fixtures are generic (AGENTS.md).
/// </summary>
public sealed class PurchaseTransactionServiceTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PurchaseTransactionServiceTests()
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

    // Each call models its own request scope: a fresh context with the two module repositories over it.
    private async Task<PurchaseRecordingResult> RecordAsync(VerifiedPurchase purchase, DateTimeOffset at)
    {
        await using var context = CreateContext();
        var service = new PurchaseTransactionService(
            new PurchaseTransactionRepository(context),
            new PurchaseEventRepository(context));
        return await service.RecordVerifiedPurchaseAsync(purchase, at, CancellationToken.None);
    }

    private async Task<PurchaseStatusChangeOutcome> ChangeStatusAsync(
        PurchaseProvider provider, string transactionId, PurchaseTransactionStatus status, DateTimeOffset at)
    {
        await using var context = CreateContext();
        var service = new PurchaseTransactionService(
            new PurchaseTransactionRepository(context),
            new PurchaseEventRepository(context));
        return await service.ChangeStatusAsync(provider, transactionId, status, at, CancellationToken.None);
    }

    private async Task<IReadOnlyList<PurchaseEvent>> TrailAsync(Guid transactionId)
    {
        await using var context = CreateContext();
        return await new PurchaseEventRepository(context).ListByTransactionAsync(transactionId, CancellationToken.None);
    }

    private async Task<int> TransactionCountAsync()
    {
        await using var context = CreateContext();
        return await context.PurchaseTransactions.AsNoTracking().CountAsync();
    }

    private static VerifiedPurchase Purchase(
        PurchaseProvider provider = PurchaseProvider.Apple,
        string transactionId = "txn-1",
        string product = "product.premium")
        => VerifiedPurchase.Create(provider, transactionId, product);

    // --- Record: persistence + initial audit ------------------------------------

    [Fact]
    public async Task Recording_a_verified_purchase_persists_it_and_audits_its_initial_state()
    {
        var result = await RecordAsync(Purchase(PurchaseProvider.Google, "order-1", "product.plus"), _at);

        Assert.Equal(PurchaseRecordingOutcome.Recorded, result.Outcome);
        Assert.Equal(PurchaseTransactionStatus.Active, result.Transaction.Status);
        Assert.Equal(1, await TransactionCountAsync());

        var trail = await TrailAsync(result.Transaction.Id);
        var only = Assert.Single(trail);
        Assert.Null(only.PreviousStatus);
        Assert.Equal(PurchaseTransactionStatus.Active, only.NewStatus);
    }

    // --- Idempotency ------------------------------------------------------------

    [Fact]
    public async Task Recording_the_same_verified_purchase_twice_is_idempotent()
    {
        var first = await RecordAsync(Purchase(PurchaseProvider.Apple, "txn-idem"), _at);
        var second = await RecordAsync(Purchase(PurchaseProvider.Apple, "txn-idem", "product.other"), _at.AddHours(1));

        Assert.Equal(PurchaseRecordingOutcome.Recorded, first.Outcome);
        Assert.Equal(PurchaseRecordingOutcome.AlreadyRecorded, second.Outcome);
        // The repeat returns the canonical existing transaction and writes neither a second row nor a duplicate
        // audit event.
        Assert.Equal(first.Transaction.Id, second.Transaction.Id);
        Assert.Equal(1, await TransactionCountAsync());
        Assert.Single(await TrailAsync(first.Transaction.Id));
    }

    // --- Status change: audited, idempotent, fail-closed ------------------------

    [Fact]
    public async Task A_status_change_is_persisted_and_audited()
    {
        var recorded = await RecordAsync(Purchase(PurchaseProvider.Apple, "txn-refund"), _at);

        var outcome = await ChangeStatusAsync(PurchaseProvider.Apple, "txn-refund", PurchaseTransactionStatus.Refunded, _at.AddHours(1));

        Assert.Equal(PurchaseStatusChangeOutcome.Changed, outcome);

        // The trail now holds the initial recording plus the audited status change (membership, not positional —
        // the two may share a millisecond and have a stable but unspecified relative order).
        var trail = await TrailAsync(recorded.Transaction.Id);
        Assert.Equal(2, trail.Count);
        Assert.Contains(trail, e => e.PreviousStatus is null && e.NewStatus == PurchaseTransactionStatus.Active);
        Assert.Contains(trail, e => e.PreviousStatus == PurchaseTransactionStatus.Active && e.NewStatus == PurchaseTransactionStatus.Refunded);

        await using var context = CreateContext();
        var stored = await new PurchaseTransactionRepository(context)
            .FindByProviderTransactionAsync(PurchaseProvider.Apple, "txn-refund", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(PurchaseTransactionStatus.Refunded, stored.Status);
    }

    [Fact]
    public async Task A_status_change_to_the_current_status_writes_no_audit_event()
    {
        var recorded = await RecordAsync(Purchase(PurchaseProvider.Apple, "txn-noop"), _at);

        var outcome = await ChangeStatusAsync(PurchaseProvider.Apple, "txn-noop", PurchaseTransactionStatus.Active, _at.AddHours(1));

        Assert.Equal(PurchaseStatusChangeOutcome.Unchanged, outcome);
        Assert.Single(await TrailAsync(recorded.Transaction.Id)); // only the initial recording event
    }

    [Fact]
    public async Task A_status_change_for_an_unknown_purchase_fails_closed()
    {
        var outcome = await ChangeStatusAsync(PurchaseProvider.Apple, "txn-missing", PurchaseTransactionStatus.Cancelled, _at);

        Assert.Equal(PurchaseStatusChangeOutcome.TransactionNotFound, outcome);
    }

    // --- Full lifecycle is reconstructable from the trail -----------------------

    [Fact]
    public async Task The_full_purchase_lifecycle_is_reconstructable_from_the_audit_trail()
    {
        var recorded = await RecordAsync(Purchase(PurchaseProvider.Google, "order-life"), _at);
        await ChangeStatusAsync(PurchaseProvider.Google, "order-life", PurchaseTransactionStatus.InGracePeriod, _at.AddHours(1));
        await ChangeStatusAsync(PurchaseProvider.Google, "order-life", PurchaseTransactionStatus.Cancelled, _at.AddHours(2));

        var trail = await TrailAsync(recorded.Transaction.Id);

        // Every state change is an auditable fact: the recording and both transitions are present. The chain is
        // reconstructable by following previous -> new (the recording starts the chain with no previous status),
        // independent of the within-millisecond storage order.
        Assert.Equal(3, trail.Count);
        var transitions = trail.Select(e => (e.PreviousStatus, e.NewStatus)).ToHashSet();
        Assert.Contains(((PurchaseTransactionStatus?)null, PurchaseTransactionStatus.Active), transitions);
        Assert.Contains(((PurchaseTransactionStatus?)PurchaseTransactionStatus.Active, PurchaseTransactionStatus.InGracePeriod), transitions);
        Assert.Contains(((PurchaseTransactionStatus?)PurchaseTransactionStatus.InGracePeriod, PurchaseTransactionStatus.Cancelled), transitions);
    }
}
