// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="BillingAccountLinkRepository"/> (CORE-MON-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so
/// the real model mapping, the SQL translation, the unique <c>billing_account_links(purchase_transaction_id)</c>
/// index, the <c>purchase_transactions</c> foreign key and the enum string conversion are exercised on every run
/// without any database server. The security-relevant negative case is the one-subject-per-receipt guarantee: the
/// unique index must reject a second link for the same purchase fail-closed, so the same external receipt can
/// never be claimed by two different subjects. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class BillingAccountLinkRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public BillingAccountLinkRepositoryTests()
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

    // Seeds a verified purchase row (the foreign key target) and returns its id.
    private async Task<Guid> SeedPurchaseAsync(string transactionId = "txn-1", PurchaseProvider provider = PurchaseProvider.Apple)
    {
        await using var context = CreateContext();
        var transaction = PurchaseTransaction.Record(VerifiedPurchase.Create(provider, transactionId, "product.premium"), _at);
        await new PurchaseTransactionRepository(context).AddAsync(transaction, CancellationToken.None);
        return transaction.Id;
    }

    // Seeds a verified purchase (in the given status) AND its buyer link to the subject — the joined state
    // ListLinkedPurchasesBySubjectAsync reads.
    private async Task SeedLinkedPurchaseAsync(
        string transactionId,
        Guid subjectId,
        string productReference,
        PurchaseTransactionStatus status,
        EntitlementSubjectType subjectType = EntitlementSubjectType.User)
    {
        await using var context = CreateContext();
        var transaction = PurchaseTransaction.Record(
            VerifiedPurchase.Create(PurchaseProvider.Apple, transactionId, productReference), _at);
        if (status != PurchaseTransactionStatus.Active)
        {
            transaction.ChangeStatus(status, _at.AddMinutes(1));
        }

        await new PurchaseTransactionRepository(context).AddAsync(transaction, CancellationToken.None);
        await new BillingAccountLinkRepository(context).AddAsync(
            BillingAccountLink.Link(transaction.Id, subjectType, subjectId, _at), CancellationToken.None);
    }

    // --- Round-trip -------------------------------------------------------------

    [Fact]
    public async Task Link_round_trips_through_the_database()
    {
        var purchaseId = await SeedPurchaseAsync("order-42", PurchaseProvider.Google);
        var subjectId = Guid.CreateVersion7();
        var link = BillingAccountLink.Link(purchaseId, EntitlementSubjectType.User, subjectId, _at);

        await using (var context = CreateContext())
        {
            Assert.Equal(
                BillingAccountLinkAddResult.Added,
                await new BillingAccountLinkRepository(context).AddAsync(link, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var loaded = await new BillingAccountLinkRepository(context)
                .FindByPurchaseTransactionAsync(purchaseId, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(link.Id, loaded.Id);
            Assert.Equal(purchaseId, loaded.PurchaseTransactionId);
            Assert.Equal(EntitlementSubjectType.User, loaded.SubjectType);
            Assert.Equal(subjectId, loaded.SubjectId);
            Assert.Equal(_at, loaded.LinkedAt);
        }
    }

    // --- One subject per receipt (duplicate guard) ------------------------------

    [Fact]
    public async Task Linking_the_same_purchase_twice_is_rejected_as_a_duplicate()
    {
        var purchaseId = await SeedPurchaseAsync("txn-dup");

        await using var context = CreateContext();
        var repository = new BillingAccountLinkRepository(context);
        Assert.Equal(
            BillingAccountLinkAddResult.Added,
            await repository.AddAsync(
                BillingAccountLink.Link(purchaseId, EntitlementSubjectType.User, Guid.CreateVersion7(), _at),
                CancellationToken.None));

        // A second link for the SAME purchase — even for a different subject — is rejected by the unique
        // purchase_transaction_id index fail-closed, so a receipt can never be claimed by two subjects.
        var result = await repository.AddAsync(
            BillingAccountLink.Link(purchaseId, EntitlementSubjectType.User, Guid.CreateVersion7(), _at),
            CancellationToken.None);

        Assert.Equal(BillingAccountLinkAddResult.DuplicatePurchase, result);
        Assert.Single(await context.BillingAccountLinks.AsNoTracking().ToListAsync());
    }

    // --- Foreign key ------------------------------------------------------------

    [Fact]
    public async Task Linking_a_nonexistent_purchase_is_not_a_duplicate_and_surfaces_the_failure()
    {
        // The purchase_transaction_id foreign key is enforced: a link to a purchase that does not exist is not a
        // duplicate (no row to collide with), so the repository rethrows the database failure rather than
        // masking it as a duplicate.
        await using var context = CreateContext();
        var repository = new BillingAccountLinkRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(
            BillingAccountLink.Link(Guid.CreateVersion7(), EntitlementSubjectType.User, Guid.CreateVersion7(), _at),
            CancellationToken.None));
    }

    // --- Negative lookup / guards -----------------------------------------------

    [Fact]
    public async Task FindByPurchaseTransaction_returns_null_for_an_unlinked_purchase()
    {
        var purchaseId = await SeedPurchaseAsync("txn-unlinked");

        await using var context = CreateContext();
        Assert.Null(await new BillingAccountLinkRepository(context)
            .FindByPurchaseTransactionAsync(purchaseId, CancellationToken.None));
    }

    [Fact]
    public async Task FindByPurchaseTransaction_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new BillingAccountLinkRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindByPurchaseTransactionAsync(Guid.Empty, CancellationToken.None));
    }

    // --- Subject lookup (CORE-MON-012) ------------------------------------------

    [Fact]
    public async Task ListLinkedPurchasesBySubject_returns_only_that_subjects_purchases_with_status_and_product()
    {
        var subjectA = Guid.CreateVersion7();
        var subjectB = Guid.CreateVersion7();
        await SeedLinkedPurchaseAsync("txn-a1", subjectA, "product.alpha", PurchaseTransactionStatus.Active);
        await SeedLinkedPurchaseAsync("txn-a2", subjectA, "product.beta", PurchaseTransactionStatus.Refunded);
        await SeedLinkedPurchaseAsync("txn-b1", subjectB, "product.alpha", PurchaseTransactionStatus.Active);

        await using var context = CreateContext();
        var results = await new BillingAccountLinkRepository(context)
            .ListLinkedPurchasesBySubjectAsync(EntitlementSubjectType.User, subjectA, CancellationToken.None);

        // Only subject A's two purchases, with their product reference and current status — never subject B's
        // (subject isolation, threat T5).
        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.ProductReference == "product.alpha" && p.Status == PurchaseTransactionStatus.Active);
        Assert.Contains(results, p => p.ProductReference == "product.beta" && p.Status == PurchaseTransactionStatus.Refunded);
    }

    [Fact]
    public async Task ListLinkedPurchasesBySubject_returns_empty_for_a_subject_with_no_purchases()
    {
        await using var context = CreateContext();
        var results = await new BillingAccountLinkRepository(context)
            .ListLinkedPurchasesBySubjectAsync(EntitlementSubjectType.User, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ListLinkedPurchasesBySubject_does_not_match_a_workspace_subject_sharing_the_id()
    {
        // The subject id is polymorphic: a User subject and a Workspace subject sharing a guid must never collide —
        // isolation is by the (subject_type, subject_id) pair.
        var sharedId = Guid.CreateVersion7();
        await SeedLinkedPurchaseAsync("txn-user", sharedId, "product.alpha", PurchaseTransactionStatus.Active, EntitlementSubjectType.User);

        await using var context = CreateContext();
        var asWorkspace = await new BillingAccountLinkRepository(context)
            .ListLinkedPurchasesBySubjectAsync(EntitlementSubjectType.Workspace, sharedId, CancellationToken.None);

        Assert.Empty(asWorkspace);
    }

    [Fact]
    public async Task ListLinkedPurchasesBySubject_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new BillingAccountLinkRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.ListLinkedPurchasesBySubjectAsync(EntitlementSubjectType.User, Guid.Empty, CancellationToken.None));
    }
}
