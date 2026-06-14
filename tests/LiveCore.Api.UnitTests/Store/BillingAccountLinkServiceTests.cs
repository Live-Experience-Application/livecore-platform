using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for <see cref="BillingAccountLinkService"/> (CORE-MON-002) — the headline buyer-linkage
/// behavior and the one-subject-per-receipt rule. The service links a recorded purchase to the authenticated buyer
/// and enforces that the same external receipt cannot be claimed by two different subjects.
///
/// The tests run the service over the real repository on an in-memory SQLite database (a verified purchase is
/// seeded first so the foreign key is satisfied), modelling each call as its own request scope. They pin the
/// acceptance criterion: a first link records the buyer (<see cref="BillingAccountLinkOutcome.Linked"/>); the SAME
/// buyer re-submitting is idempotent (<see cref="BillingAccountLinkOutcome.AlreadyLinked"/>, no second row); a
/// DIFFERENT subject is denied fail-closed (<see cref="BillingAccountLinkOutcome.ConflictDifferentSubject"/>, the
/// link stays the first buyer's) — "user B cannot bind user A's transaction/receipt". All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class BillingAccountLinkServiceTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public BillingAccountLinkServiceTests()
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

    private async Task<Guid> SeedPurchaseAsync(string transactionId = "txn-1")
    {
        await using var context = CreateContext();
        var transaction = PurchaseTransaction.Record(
            VerifiedPurchase.Create(PurchaseProvider.Apple, transactionId, "product.premium"), _at);
        await new PurchaseTransactionRepository(context).AddAsync(transaction, CancellationToken.None);
        return transaction.Id;
    }

    private async Task<BillingAccountLinkResult> LinkAsync(Guid purchaseId, Guid subjectId, DateTimeOffset at)
    {
        await using var context = CreateContext();
        var service = new BillingAccountLinkService(new BillingAccountLinkRepository(context));
        return await service.LinkBuyerAsync(purchaseId, EntitlementSubjectType.User, subjectId, at, CancellationToken.None);
    }

    private async Task<int> LinkCountAsync()
    {
        await using var context = CreateContext();
        return await context.BillingAccountLinks.AsNoTracking().CountAsync();
    }

    // --- First link -------------------------------------------------------------

    [Fact]
    public async Task Linking_a_purchase_records_the_buyer()
    {
        var purchaseId = await SeedPurchaseAsync("order-1");
        var buyer = Guid.CreateVersion7();

        var result = await LinkAsync(purchaseId, buyer, _at);

        Assert.Equal(BillingAccountLinkOutcome.Linked, result.Outcome);
        Assert.Equal(purchaseId, result.Link.PurchaseTransactionId);
        Assert.Equal(EntitlementSubjectType.User, result.Link.SubjectType);
        Assert.Equal(buyer, result.Link.SubjectId);
        Assert.Equal(1, await LinkCountAsync());
    }

    // --- Idempotency (same buyer) -----------------------------------------------

    [Fact]
    public async Task Linking_the_same_purchase_to_the_same_buyer_is_idempotent()
    {
        var purchaseId = await SeedPurchaseAsync("order-idem");
        var buyer = Guid.CreateVersion7();

        var first = await LinkAsync(purchaseId, buyer, _at);
        var second = await LinkAsync(purchaseId, buyer, _at.AddHours(1));

        Assert.Equal(BillingAccountLinkOutcome.Linked, first.Outcome);
        Assert.Equal(BillingAccountLinkOutcome.AlreadyLinked, second.Outcome);
        // The repeat returns the canonical existing link and writes no second row.
        Assert.Equal(first.Link.Id, second.Link.Id);
        Assert.Equal(1, await LinkCountAsync());
    }

    // --- Cross-subject denial (the crown jewel) ---------------------------------

    [Fact]
    public async Task A_different_subject_cannot_claim_an_already_linked_purchase()
    {
        var purchaseId = await SeedPurchaseAsync("order-shared");
        var buyerA = Guid.CreateVersion7();
        var buyerB = Guid.CreateVersion7();

        var linkedToA = await LinkAsync(purchaseId, buyerA, _at);
        var bAttempt = await LinkAsync(purchaseId, buyerB, _at.AddHours(1));

        Assert.Equal(BillingAccountLinkOutcome.Linked, linkedToA.Outcome);
        // Fail-closed: the receipt is already A's, so B is denied and nothing new is written — the existing link
        // still belongs to A (user B cannot bind user A's receipt).
        Assert.Equal(BillingAccountLinkOutcome.ConflictDifferentSubject, bAttempt.Outcome);
        Assert.Equal(buyerA, bAttempt.Link.SubjectId);
        Assert.Equal(1, await LinkCountAsync());

        await using var context = CreateContext();
        var stored = await new BillingAccountLinkRepository(context)
            .FindByPurchaseTransactionAsync(purchaseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(buyerA, stored.SubjectId);
    }

    // --- Guards -----------------------------------------------------------------

    [Fact]
    public async Task LinkBuyer_rejects_an_empty_purchase_id()
    {
        await using var context = CreateContext();
        var service = new BillingAccountLinkService(new BillingAccountLinkRepository(context));

        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkBuyerAsync(
            Guid.Empty, EntitlementSubjectType.User, Guid.CreateVersion7(), _at, CancellationToken.None));
    }

    [Fact]
    public async Task LinkBuyer_rejects_an_empty_subject_id()
    {
        var purchaseId = await SeedPurchaseAsync("order-guard");

        await using var context = CreateContext();
        var service = new BillingAccountLinkService(new BillingAccountLinkRepository(context));

        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkBuyerAsync(
            purchaseId, EntitlementSubjectType.User, Guid.Empty, _at, CancellationToken.None));
    }
}
