using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="PurchaseTransactionRepository"/> (CORE-STORE-002).
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so
/// the real model mapping, the SQL translation, the unique (provider, provider_transaction_id) index and the
/// enum string conversions are exercised on every run without any database server. The behaviors under test —
/// round-trip, the IDEMPOTENCY duplicate guard, the provider being part of the key, in-place status update and
/// the negative/blank lookup — are relational semantics shared with PostgreSQL.
///
/// The security-relevant negative case here is idempotency: the unique index must reject a second recording of
/// the same verified purchase fail-closed, so a client retry or a replayed proof can never create a duplicate
/// transaction. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class PurchaseTransactionRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _recordedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PurchaseTransactionRepositoryTests()
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

    private static PurchaseTransaction Transaction(
        PurchaseProvider provider = PurchaseProvider.Apple,
        string transactionId = "txn-1",
        string product = "product.premium")
        => PurchaseTransaction.Record(VerifiedPurchase.Create(provider, transactionId, product), _recordedAt);

    // --- Round-trip -------------------------------------------------------------

    [Fact]
    public async Task Transaction_round_trips_through_the_database()
    {
        var transaction = Transaction(PurchaseProvider.Google, "order-42", "product.plus");

        await using (var context = CreateContext())
        {
            var repository = new PurchaseTransactionRepository(context);
            Assert.Equal(PurchaseTransactionAddResult.Added, await repository.AddAsync(transaction, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var repository = new PurchaseTransactionRepository(context);
            var loaded = await repository.FindByProviderTransactionAsync(
                PurchaseProvider.Google, "order-42", CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(transaction.Id, loaded.Id);
            Assert.Equal(PurchaseProvider.Google, loaded.Provider);
            Assert.Equal("order-42", loaded.ProviderTransactionId);
            Assert.Equal("product.plus", loaded.ProductReference);
            Assert.Equal(PurchaseTransactionStatus.Active, loaded.Status);
            Assert.Equal(_recordedAt, loaded.RecordedAt);
        }
    }

    // --- Idempotency (duplicate guard) ------------------------------------------

    [Fact]
    public async Task Recording_the_same_provider_transaction_twice_is_rejected_as_a_duplicate()
    {
        await using var context = CreateContext();
        var repository = new PurchaseTransactionRepository(context);
        Assert.Equal(
            PurchaseTransactionAddResult.Added,
            await repository.AddAsync(Transaction(PurchaseProvider.Apple, "txn-dup"), CancellationToken.None));

        // A different surrogate row, same (provider, provider transaction id): the unique index rejects it
        // fail-closed rather than creating a duplicate purchase.
        var result = await repository.AddAsync(
            Transaction(PurchaseProvider.Apple, "txn-dup", "product.other"), CancellationToken.None);

        Assert.Equal(PurchaseTransactionAddResult.DuplicateTransaction, result);
        Assert.Single(await context.PurchaseTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_same_transaction_id_under_different_providers_is_not_a_duplicate()
    {
        await using var context = CreateContext();
        var repository = new PurchaseTransactionRepository(context);

        // The unique key includes the provider, so the same provider transaction id under Apple and under Google
        // are two distinct purchases.
        Assert.Equal(
            PurchaseTransactionAddResult.Added,
            await repository.AddAsync(Transaction(PurchaseProvider.Apple, "shared-id"), CancellationToken.None));
        Assert.Equal(
            PurchaseTransactionAddResult.Added,
            await repository.AddAsync(Transaction(PurchaseProvider.Google, "shared-id"), CancellationToken.None));

        Assert.NotNull(await repository.FindByProviderTransactionAsync(PurchaseProvider.Apple, "shared-id", CancellationToken.None));
        Assert.NotNull(await repository.FindByProviderTransactionAsync(PurchaseProvider.Google, "shared-id", CancellationToken.None));
    }

    // --- Update path ------------------------------------------------------------

    [Fact]
    public async Task Update_persists_a_status_change_in_place()
    {
        var transaction = Transaction(PurchaseProvider.Apple, "txn-status");
        await using (var context = CreateContext())
        {
            await new PurchaseTransactionRepository(context).AddAsync(transaction, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new PurchaseTransactionRepository(context);
            var loaded = await repository.FindByProviderTransactionAsync(PurchaseProvider.Apple, "txn-status", CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.True(loaded.ChangeStatus(PurchaseTransactionStatus.Cancelled, _recordedAt.AddHours(1)));
            await repository.UpdateAsync(loaded, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var loaded = await new PurchaseTransactionRepository(context)
                .FindByProviderTransactionAsync(PurchaseProvider.Apple, "txn-status", CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(PurchaseTransactionStatus.Cancelled, loaded.Status);
            Assert.Single(await context.PurchaseTransactions.AsNoTracking().ToListAsync()); // updated in place, not duplicated
        }
    }

    // --- Negative lookup / guards -----------------------------------------------

    [Fact]
    public async Task FindByProviderTransaction_returns_null_for_an_unknown_purchase()
    {
        await using var context = CreateContext();
        var repository = new PurchaseTransactionRepository(context);
        await repository.AddAsync(Transaction(PurchaseProvider.Apple, "txn-known"), CancellationToken.None);

        Assert.Null(await repository.FindByProviderTransactionAsync(PurchaseProvider.Apple, "txn-unknown", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindByProviderTransaction_rejects_a_blank_transaction_id(string transactionId)
    {
        await using var context = CreateContext();
        var repository = new PurchaseTransactionRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.FindByProviderTransactionAsync(
            PurchaseProvider.Apple, transactionId, CancellationToken.None));
    }
}
