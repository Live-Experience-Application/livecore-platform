// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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

    // --- Concurrency conflict keeps the shared context usable (CORE-CONC-007) ----

    [Fact]
    public async Task Update_that_loses_a_concurrency_race_detaches_the_row_and_does_not_poison_the_context()
    {
        // Two purchases. The worker reconciliation sweep reuses ONE scoped context across a whole batch, so a
        // conflict on one purchase must not break the next purchase's update on the same context.
        await using (var context = CreateContext())
        {
            var seed = new PurchaseTransactionRepository(context);
            await seed.AddAsync(Transaction(PurchaseProvider.Apple, "txn-conflict"), CancellationToken.None);
            await seed.AddAsync(Transaction(PurchaseProvider.Apple, "txn-ok"), CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new PurchaseTransactionRepository(context);

            // Load and mutate the first purchase (the tracked read-modify-write a status change performs).
            var conflicting = await repository.FindByProviderTransactionAsync(
                PurchaseProvider.Apple, "txn-conflict", CancellationToken.None);
            Assert.NotNull(conflicting);
            Assert.True(conflicting.ChangeStatus(PurchaseTransactionStatus.Cancelled, _recordedAt.AddHours(1)));

            // Simulate a concurrent writer removing the row out from under the tracked update, so the UPDATE matches
            // zero rows and EF raises a genuine DbUpdateConcurrencyException — the same loud conflict the PostgreSQL
            // xmin token raises on an interleaved write (the SQLite test provider carries no token).
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM purchase_transactions WHERE provider_transaction_id = 'txn-conflict';");

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => repository.UpdateAsync(conflicting, CancellationToken.None));

            // The conflicted entity is detached, so its abandoned change is not re-sent by a later SaveChanges on the
            // same context.
            Assert.Equal(EntityState.Detached, context.Entry(conflicting).State);

            // A DIFFERENT purchase can still be updated on the SAME context — proof the conflict did not poison the
            // batch. Without the detach this SaveChanges would also re-attempt the deleted row and throw again.
            var other = await repository.FindByProviderTransactionAsync(
                PurchaseProvider.Apple, "txn-ok", CancellationToken.None);
            Assert.NotNull(other);
            Assert.True(other.ChangeStatus(PurchaseTransactionStatus.Refunded, _recordedAt.AddHours(2)));
            await repository.UpdateAsync(other, CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var repository = new PurchaseTransactionRepository(context);

            // The second purchase was updated cleanly...
            var other = await repository.FindByProviderTransactionAsync(
                PurchaseProvider.Apple, "txn-ok", CancellationToken.None);
            Assert.NotNull(other);
            Assert.Equal(PurchaseTransactionStatus.Refunded, other.Status);

            // ...and the lost update was NOT replayed onto the row the concurrent writer removed.
            Assert.Null(await repository.FindByProviderTransactionAsync(
                PurchaseProvider.Apple, "txn-conflict", CancellationToken.None));
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
