using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// EF Core implementation of <see cref="IPurchaseTransactionRepository"/> (CORE-STORE-002), backed by the
/// <c>purchase_transactions</c> table mapped in <see cref="PurchaseTransactionConfiguration"/>. The unique
/// (provider, provider_transaction_id) index is the idempotency guarantee; a duplicate insert is surfaced as a
/// result rather than an exception.
/// </summary>
internal sealed class PurchaseTransactionRepository : IPurchaseTransactionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public PurchaseTransactionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<PurchaseTransactionAddResult> AddAsync(
        PurchaseTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _dbContext.PurchaseTransactions.Add(transaction);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return PurchaseTransactionAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope.
            _dbContext.Entry(transaction).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if a transaction for the same (provider, provider
            // transaction id) already exists, the unique index rejected the insert as a duplicate (a client
            // retry, a replayed proof, or a lost race). The table has no other unique constraint and no foreign
            // key, so any other failure is rethrown unchanged.
            var duplicateExists = await _dbContext.PurchaseTransactions
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.Provider == transaction.Provider
                        && existing.ProviderTransactionId == transaction.ProviderTransactionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return PurchaseTransactionAddResult.DuplicateTransaction;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PurchaseTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // The transaction is already tracked (it was loaded through this context); EF persists the mutated
        // status and timestamp columns. The update is in place — a purchase is one row.
        _dbContext.PurchaseTransactions.Update(transaction);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PurchaseTransaction?> FindByProviderTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken)
    {
        // A blank id can never address a stored purchase, so the lookup fails fast instead of matching an
        // arbitrary row.
        if (string.IsNullOrWhiteSpace(providerTransactionId))
        {
            throw new ArgumentException("Provider transaction id must not be empty.", nameof(providerTransactionId));
        }

        var normalized = providerTransactionId.Trim();
        return await _dbContext.PurchaseTransactions
            .FirstOrDefaultAsync(
                transaction => transaction.Provider == provider
                    && transaction.ProviderTransactionId == normalized,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
