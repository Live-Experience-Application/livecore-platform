namespace LiveCore.Api.SystemModule;

/// <summary>
/// Persistence contract for idempotency keys (CORE-VIS-004). The System module owns the
/// <c>idempotency_keys</c> table; features that need retry safety (the reveal command first) use this
/// store to detect and reject duplicate processing of a client request.
///
/// The contract is two operations: <see cref="FindAsync"/> recognizes a retry (the key was already
/// recorded), and <see cref="AddAsync"/> records a key and reports whether it was new or a duplicate
/// (the unique <c>idempotency_keys(scope, key)</c> index decides). Together they give an idempotent
/// "process once" guard: a feature checks for an existing key, performs its (state-idempotent) effect
/// if absent, then records the key — the unique index closing the concurrent-retry race.
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
}
