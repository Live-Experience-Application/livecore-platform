using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// EF Core implementation of <see cref="IPurchaseEventRepository"/> (CORE-STORE-002), backed by the append-only
/// <c>purchase_events</c> table mapped in <see cref="PurchaseEventConfiguration"/>. There is no update or delete
/// path: purchase events are immutable once written.
/// </summary>
internal sealed class PurchaseEventRepository : IPurchaseEventRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public PurchaseEventRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(PurchaseEvent purchaseEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purchaseEvent);

        _dbContext.PurchaseEvents.Add(purchaseEvent);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PurchaseEvent>> ListByTransactionAsync(
        Guid purchaseTransactionId,
        CancellationToken cancellationToken)
    {
        // An empty id can never address a stored transaction's events, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (purchaseTransactionId == Guid.Empty)
        {
            throw new ArgumentException("Purchase transaction id must not be empty.", nameof(purchaseTransactionId));
        }

        // Scoped by the owning transaction and ordered by the time-ordered surrogate id (UUIDv7), which is
        // chronological and — unlike ordering by the DateTimeOffset created_at — is supported by every provider
        // (SQLite cannot ORDER BY a DateTimeOffset); this matches the ordering convention of the audit log and
        // the session event stream. Two events created within the same millisecond have a stable but unspecified
        // relative order; the created_at column backs the documented time-ordered index. In production a
        // purchase's state changes occur at genuinely different times, so the read is chronological.
        return await _dbContext.PurchaseEvents
            .Where(purchaseEvent => purchaseEvent.PurchaseTransactionId == purchaseTransactionId)
            .OrderBy(purchaseEvent => purchaseEvent.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
