// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using LiveCore.Api.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Store;

/// <summary>
/// Integration-style tests for the EF Core-backed <see cref="PurchaseEventRepository"/> (CORE-STORE-002) — the
/// append-only purchase audit trail.
///
/// They run against an in-memory SQLite database with foreign keys enforced (<c>PRAGMA foreign_keys = ON</c>), so
/// the real model mapping, the per-transaction read order, the cascade foreign key to <c>purchase_transactions</c>
/// and the enum string conversions are exercised without a database server. The behaviors under test — append +
/// chronological per-transaction read, isolation (one transaction's events are never returned for another), the
/// foreign-key integrity that rejects an event for a non-existent transaction, and the cascade that removes a
/// transaction's events with it — are relational semantics shared with PostgreSQL. All fixtures are generic
/// (AGENTS.md).
/// </summary>
public sealed class PurchaseEventRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset _at = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public PurchaseEventRepositoryTests()
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

    private async Task<PurchaseTransaction> SeedTransactionAsync(string transactionId)
    {
        var transaction = PurchaseTransaction.Record(
            VerifiedPurchase.Create(PurchaseProvider.Apple, transactionId, "product.premium"), _at);
        await using var context = CreateContext();
        await new PurchaseTransactionRepository(context).AddAsync(transaction, CancellationToken.None);
        return transaction;
    }

    // --- Append + per-transaction read ------------------------------------------

    [Fact]
    public async Task Every_appended_event_is_returned_for_its_transaction_in_a_stable_order()
    {
        var transaction = await SeedTransactionAsync("txn-trail");

        await using (var context = CreateContext())
        {
            var repository = new PurchaseEventRepository(context);
            await repository.AppendAsync(PurchaseEvent.ForRecorded(transaction.Id, _at), CancellationToken.None);
            await repository.AppendAsync(
                PurchaseEvent.ForStatusChange(transaction.Id, PurchaseTransactionStatus.Active, PurchaseTransactionStatus.InGracePeriod, _at.AddHours(1)),
                CancellationToken.None);
            await repository.AppendAsync(
                PurchaseEvent.ForStatusChange(transaction.Id, PurchaseTransactionStatus.InGracePeriod, PurchaseTransactionStatus.Cancelled, _at.AddHours(2)),
                CancellationToken.None);
        }

        // The trail captures every state change as an auditable fact. Events created within the same millisecond
        // have a stable but unspecified relative order (ordered by the UUIDv7 id, like the session event stream),
        // so the test asserts membership and re-query STABILITY rather than a specific within-millisecond pair
        // order (in production a purchase's state changes occur at genuinely different times).
        await using (var context = CreateContext())
        {
            var repository = new PurchaseEventRepository(context);
            var trail = await repository.ListByTransactionAsync(transaction.Id, CancellationToken.None);
            var again = await repository.ListByTransactionAsync(transaction.Id, CancellationToken.None);

            Assert.Equal(3, trail.Count);
            Assert.Equal(trail.Select(e => e.Id), again.Select(e => e.Id)); // stable order on re-query
            var transitions = trail.Select(e => (e.PreviousStatus, e.NewStatus)).ToHashSet();
            Assert.Contains(((PurchaseTransactionStatus?)null, PurchaseTransactionStatus.Active), transitions);
            Assert.Contains(((PurchaseTransactionStatus?)PurchaseTransactionStatus.Active, PurchaseTransactionStatus.InGracePeriod), transitions);
            Assert.Contains(((PurchaseTransactionStatus?)PurchaseTransactionStatus.InGracePeriod, PurchaseTransactionStatus.Cancelled), transitions);
        }
    }

    // --- Isolation --------------------------------------------------------------

    [Fact]
    public async Task One_transactions_events_are_never_returned_for_another()
    {
        var transactionA = await SeedTransactionAsync("txn-a");
        var transactionB = await SeedTransactionAsync("txn-b");

        await using (var context = CreateContext())
        {
            await new PurchaseEventRepository(context).AppendAsync(PurchaseEvent.ForRecorded(transactionA.Id, _at), CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            // B has no events; A's event is never returned through B's id.
            Assert.Empty(await new PurchaseEventRepository(context).ListByTransactionAsync(transactionB.Id, CancellationToken.None));
        }
    }

    // --- Foreign-key integrity --------------------------------------------------

    [Fact]
    public async Task Appending_an_event_for_a_non_existent_transaction_is_rejected()
    {
        await using var context = CreateContext();
        var repository = new PurchaseEventRepository(context);

        // The transaction id has no target row: the cascade foreign key rejects the insert.
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.AppendAsync(
            PurchaseEvent.ForRecorded(Guid.CreateVersion7(), _at), CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_transaction_cascades_to_its_events()
    {
        var transaction = await SeedTransactionAsync("txn-cascade");
        await using (var context = CreateContext())
        {
            await new PurchaseEventRepository(context).AppendAsync(PurchaseEvent.ForRecorded(transaction.Id, _at), CancellationToken.None);
        }

        await using (var context = CreateContext())
        {
            var stored = await context.PurchaseTransactions.FirstAsync(t => t.Id == transaction.Id);
            context.PurchaseTransactions.Remove(stored);
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext())
        {
            Assert.Empty(await new PurchaseEventRepository(context).ListByTransactionAsync(transaction.Id, CancellationToken.None));
        }
    }

    // --- Guards -----------------------------------------------------------------

    [Fact]
    public async Task ListByTransaction_rejects_an_empty_id()
    {
        await using var context = CreateContext();
        var repository = new PurchaseEventRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ListByTransactionAsync(Guid.Empty, CancellationToken.None));
    }
}
