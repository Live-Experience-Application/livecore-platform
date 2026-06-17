using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.SystemModule;

/// <summary>
/// EF Core implementation of <see cref="IIdempotencyKeyStore"/> (CORE-VIS-004), backed by the
/// <c>idempotency_keys</c> table mapped in <see cref="IdempotencyKeyConfiguration"/>.
/// </summary>
internal sealed class IdempotencyKeyStore : IIdempotencyKeyStore
{
    private readonly LiveCoreDbContext _dbContext;

    public IdempotencyKeyStore(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<IdempotencyKey?> FindAsync(string scope, string key, CancellationToken cancellationToken)
    {
        // Blank values can never address a stored key, so the lookup fails fast instead of matching
        // an arbitrary row.
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Scope must not be empty.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key must not be empty.", nameof(key));
        }

        return await _dbContext.IdempotencyKeys
            .FirstOrDefaultAsync(
                idempotencyKey => idempotencyKey.Scope == scope && idempotencyKey.Key == key,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IdempotencyKeyAddResult> AddAsync(
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        _dbContext.IdempotencyKeys.Add(idempotencyKey);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdempotencyKeyAddResult.Added;
        }
        catch (DbUpdateException)
        {
            // The only constraint an insert can violate here is the unique (scope, key) index (the
            // table has no foreign keys), so a failed insert means the same key was recorded
            // concurrently — report it as a duplicate so the caller treats the request as already
            // processed. Detach the rejected entity so the context stays usable.
            _dbContext.Entry(idempotencyKey).State = EntityState.Detached;
            return IdempotencyKeyAddResult.Duplicate;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteCreatedBeforeAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "The batch size must be positive.");
        }

        // SELECT the oldest candidates by their time-ordered surrogate id (UUIDv7 derived from the recorded
        // time, chronological and provider-independent — the SQLite test provider cannot ORDER BY or compare a
        // DateTimeOffset, exactly as the CORE-PRIV-003 retention reads note). Oldest first, bounded by the batch,
        // so this sweep can never do unbounded work and repeated sweeps cover the backlog without starvation.
        // Only the id and recorded time are read — never the scope or key value (threat T7) — and the age
        // threshold is applied AFTER materialization so a still-recent row that slips into the batch is excluded.
        var expiredIds = (await _dbContext.IdempotencyKeys
                .AsNoTracking()
                .OrderBy(idempotencyKey => idempotencyKey.Id)
                .Take(maxCount)
                .Select(idempotencyKey => new { idempotencyKey.Id, idempotencyKey.CreatedAt })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(candidate => candidate.CreatedAt < createdBefore)
            .Select(candidate => candidate.Id)
            .ToList();

        if (expiredIds.Count == 0)
        {
            return 0;
        }

        // Bulk DELETE the selected rows by id in one statement (no change tracker, no audit — the purge is by
        // age alone and reported by count, never by key value; CORE-PRIV-006). Idempotent and concurrency-safe:
        // a row another sweep already removed simply matches nothing here, so the count reflects what THIS sweep
        // removed and overlapping sweeps never double-delete or error.
        return await _dbContext.IdempotencyKeys
            .Where(idempotencyKey => expiredIds.Contains(idempotencyKey.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
