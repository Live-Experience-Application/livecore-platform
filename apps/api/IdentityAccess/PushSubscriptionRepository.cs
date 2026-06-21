// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// EF Core implementation of <see cref="IPushSubscriptionRepository"/> (CORE-PUSH-001), backed by the
/// <c>push_subscriptions</c> table mapped in <see cref="PushSubscriptionConfiguration"/>. Every query leads
/// with the owning principal's user-profile id, so a caller can only ever address its OWN subscriptions
/// (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal sealed class PushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public PushSubscriptionRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<PushSubscription?> FindByUserAndEndpointAsync(
        Guid userProfileId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        // Invalid inputs can never address a stored subscription (rows only exist in valid states), so the
        // lookup fails fast instead of silently returning nothing for malformed input.
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("User profile id must not be empty.", nameof(userProfileId));
        }

        if (!PushSubscription.IsValidEndpoint(endpoint))
        {
            throw new ArgumentException("Endpoint violates the push endpoint invariants.", nameof(endpoint));
        }

        return await _dbContext.PushSubscriptions
            .FirstOrDefaultAsync(
                subscription => subscription.UserProfileId == userProfileId && subscription.Endpoint == endpoint,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        _dbContext.PushSubscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(PushSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (_dbContext.Entry(subscription).State == EntityState.Detached)
        {
            _dbContext.PushSubscriptions.Update(subscription);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteByIdForUserAsync(
        Guid subscriptionId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        if (subscriptionId == Guid.Empty || userProfileId == Guid.Empty)
        {
            // An empty id can never address a stored subscription; nothing is deleted (a fail-closed miss).
            return false;
        }

        // The predicate leads with BOTH the subscription id AND the owning principal, so a foreign principal's
        // subscription matches nothing and is removed by no one but its owner (threats T1/T5). FirstOrDefault +
        // Remove (rather than a set-based delete) keeps the path provider-neutral across the SQLite test
        // provider and PostgreSQL.
        var subscription = await _dbContext.PushSubscriptions
            .FirstOrDefaultAsync(
                row => row.Id == subscriptionId && row.UserProfileId == userProfileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            return false;
        }

        _dbContext.PushSubscriptions.Remove(subscription);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PushSubscription>> ListByUserAsync(
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        if (userProfileId == Guid.Empty)
        {
            throw new ArgumentException("User profile id must not be empty.", nameof(userProfileId));
        }

        return await _dbContext.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserProfileId == userProfileId)
            .OrderBy(subscription => subscription.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
