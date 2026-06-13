using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Store;

/// <summary>
/// EF Core implementation of <see cref="IStoreNotificationEventRepository"/> (CORE-STORE-005), backed by the
/// append-only <c>store_notification_events</c> table mapped in <see cref="StoreNotificationEventConfiguration"/>.
/// The unique (provider, provider_notification_id) index is the idempotency guarantee; a duplicate insert is
/// surfaced as a result rather than an exception (mirrors <see cref="PurchaseTransactionRepository"/>). There is
/// no update or delete path: notification events are immutable once written.
/// </summary>
internal sealed class StoreNotificationEventRepository : IStoreNotificationEventRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public StoreNotificationEventRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<StoreNotificationEvent?> FindByProviderNotificationAsync(
        PurchaseProvider provider,
        string providerNotificationId,
        CancellationToken cancellationToken)
    {
        // A blank id can never address a stored notification, so the lookup fails fast instead of matching an
        // arbitrary row.
        if (string.IsNullOrWhiteSpace(providerNotificationId))
        {
            throw new ArgumentException("Provider notification id must not be empty.", nameof(providerNotificationId));
        }

        var normalized = providerNotificationId.Trim();
        return await _dbContext.StoreNotificationEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                notification => notification.Provider == provider
                    && notification.ProviderNotificationId == normalized,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoreNotificationEvent?> FindLatestByProviderTransactionAsync(
        PurchaseProvider provider,
        string providerTransactionId,
        CancellationToken cancellationToken)
    {
        // A blank id can never address a stored notification, so the lookup fails fast instead of matching an
        // arbitrary row.
        if (string.IsNullOrWhiteSpace(providerTransactionId))
        {
            throw new ArgumentException("Provider transaction id must not be empty.", nameof(providerTransactionId));
        }

        var normalized = providerTransactionId.Trim();

        // Read the purchase's notifications (filtered in SQL by the (provider, provider_transaction_id) index) and
        // pick the latest in memory. A purchase has only a handful of lifecycle notifications, so materializing them
        // is cheap — and ordering happens client-side because the SQLite provider used in the test harness cannot
        // ORDER BY a DateTimeOffset column (a provider limitation, not a Postgres one). Latest is by the store's
        // reported event time, ties broken by the time-ordered (UUID v7) row id so the result is deterministic when
        // two notifications report the same occurred_at.
        var notifications = await _dbContext.StoreNotificationEvents
            .AsNoTracking()
            .Where(notification => notification.Provider == provider
                && notification.ProviderTransactionId == normalized)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return notifications
            .OrderByDescending(notification => notification.OccurredAt)
            .ThenByDescending(notification => notification.Id)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<StoreNotificationEventAddResult> AddAsync(
        StoreNotificationEvent notificationEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);

        _dbContext.StoreNotificationEvents.Add(notificationEvent);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return StoreNotificationEventAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // Keep the context usable: the failed insert must not be retried by a later SaveChanges on the same
            // scope.
            _dbContext.Entry(notificationEvent).State = EntityState.Detached;

            // Provider-neutral duplicate detection: if an event for the same (provider, provider notification id)
            // already exists, the unique index rejected the insert as a duplicate (a re-delivered notification or
            // a lost race). The table has no other unique constraint and no foreign key, so any other failure is
            // rethrown unchanged.
            var duplicateExists = await _dbContext.StoreNotificationEvents
                .AsNoTracking()
                .AnyAsync(
                    existing => existing.Provider == notificationEvent.Provider
                        && existing.ProviderNotificationId == notificationEvent.ProviderNotificationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateExists)
            {
                return StoreNotificationEventAddResult.DuplicateNotification;
            }

            throw;
        }
    }
}
