namespace LiveCore.Api.SystemModule;

/// <summary>
/// Persistence contract for idempotency keys (CORE-VIS-004). The System module owns the
/// <c>idempotency_keys</c> table; features that need retry safety (the reveal command first) use this
/// store to detect and reject duplicate processing of a client request.
///
/// The "process once" guard is two operations: <see cref="FindAsync"/> recognizes a retry (the key was
/// already recorded), and <see cref="AddAsync"/> records a key and reports whether it was new or a
/// duplicate (the unique <c>idempotency_keys(scope, key)</c> index decides). Together they give an
/// idempotent guard: a feature checks for an existing key, performs its (state-idempotent) effect if
/// absent, then records the key — the unique index closing the concurrent-retry race.
///
/// A third operation, <see cref="DeleteCreatedBeforeAsync"/>, bounds the otherwise insert-only table: the
/// background data-retention sweep removes rows older than a configurable window by AGE alone, so the table
/// cannot grow without limit over a deployment's lifetime (CORE-PRIV-006).
/// </summary>
public interface IIdempotencyKeyStore
{
    /// <summary>
    /// Finds the idempotency key recorded for exactly the given (scope, key) pair, or
    /// <see langword="null"/> when none exists. A non-null result means the request has already been
    /// processed (a retry).
    /// </summary>
    /// <exception cref="ArgumentException">The scope or key is null, empty or whitespace.</exception>
    Task<IdempotencyKey?> FindAsync(string scope, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Records a new idempotency key. Returns <see cref="IdempotencyKeyAddResult.Added"/> when the
    /// (scope, key) pair was new, or <see cref="IdempotencyKeyAddResult.Duplicate"/> when the unique
    /// index rejected it because the same pair was recorded concurrently — so the caller can treat a
    /// race as "already processed".
    /// </summary>
    Task<IdempotencyKeyAddResult> AddAsync(IdempotencyKey idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Removes up to <paramref name="maxCount"/> idempotency-key rows recorded before
    /// <paramref name="createdBefore"/> — the data-retention sweep's bounded purge that keeps the
    /// otherwise insert-only table from growing without limit (CORE-PRIV-006). Selection is by AGE ALONE
    /// (the row's recorded time, never its scope or key value), oldest first and bounded by the batch, so
    /// a single sweep can never do unbounded work and repeated sweeps cover the whole backlog without
    /// starvation. The delete is idempotent and concurrency-safe: a row another sweep already removed
    /// simply matches nothing, so overlapping sweeps never double-delete or error. Returns the number of
    /// rows actually removed (a count, never a key value; threat T7).
    /// </summary>
    /// <param name="createdBefore">The exclusive upper bound on a row's recorded time; only older rows are eligible.</param>
    /// <param name="maxCount">The maximum number of rows to remove in this sweep (the retention batch size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of idempotency-key rows removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The batch size is not positive.</exception>
    Task<int> DeleteCreatedBeforeAsync(DateTimeOffset createdBefore, int maxCount, CancellationToken cancellationToken);
}
