// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Realtime;

/// <summary>
/// EF Core implementation of <see cref="IPushNotificationDeliveryRepository"/> (CORE-PUSH-002), backed by the
/// <c>push_notification_deliveries</c> outbox table mapped in <see cref="PushNotificationDeliveryConfiguration"/>.
/// </summary>
internal sealed class PushNotificationDeliveryRepository : IPushNotificationDeliveryRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public PushNotificationDeliveryRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(
        IReadOnlyCollection<PushNotificationDelivery> deliveries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        if (deliveries.Count == 0)
        {
            return;
        }

        await _dbContext.PushNotificationDeliveries.AddRangeAsync(deliveries, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushNotificationDelivery>> ListPendingAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "The batch size must be positive.");
        }

        // FIFO drain by the time-ordered surrogate id (UUIDv7), provider-independent (SQLite cannot ORDER BY a
        // DateTimeOffset). Tracked (not AsNoTracking) so a processed row can be removed via DeleteAsync.
        return await _dbContext.PushNotificationDeliveries
            .OrderBy(delivery => delivery.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(PushNotificationDelivery delivery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        _dbContext.PushNotificationDeliveries.Remove(delivery);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
