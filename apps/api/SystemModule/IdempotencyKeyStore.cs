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
}
