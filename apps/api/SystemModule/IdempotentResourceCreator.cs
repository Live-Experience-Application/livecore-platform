using LiveCore.Api.Persistence;

namespace LiveCore.Api.SystemModule;

/// <summary>
/// Makes a resource-creating POST idempotent for an OPTIONAL client <c>Idempotency-Key</c> (CORE-DX-004). It
/// is the generic mechanism the create routes — session, scene, content-block, workspace and asset-link create
/// — share so a client/network retry under the SAME key returns the ORIGINAL result and creates EXACTLY ONE
/// resource (the acceptance criterion of CORE-DX-004), reusing the System module's
/// <see cref="IIdempotencyKeyStore"/> and the CORE-CONC-002 <see cref="TransactionalUnitOfWork"/> rather than a
/// parallel mechanism.
///
/// <para>
/// The three paths of <see cref="CreateAsync{TResult}"/>:
/// <list type="bullet">
///   <item>NO KEY — the create runs once inside a unit of work and is reported as
///   <see cref="IdempotentCreation{TResult}.Created"/>. Nothing is recorded, so the route behaves exactly as it
///   did before the header existed (a non-breaking default).</item>
///   <item>KEY, ALREADY RECORDED — the key was recorded by a prior committed request, so its bound
///   <see cref="IdempotencyKey.ResultId"/> is returned as <see cref="IdempotentCreation{TResult}.Replayed"/>;
///   the caller re-loads that resource (tenant-scoped) and returns the original result WITHOUT creating a
///   second one.</item>
///   <item>KEY, FIRST TIME — the create AND the key record (bound to the produced resource id) commit in ONE
///   transaction. The unique <c>idempotency_keys(scope, key)</c> index closes the concurrent-first-request
///   race: the loser's key insert is a duplicate, so this attempt's freshly created resource is ROLLED BACK
///   (never a double-create) and the request becomes a replay of the winner's resource.</item>
/// </list>
/// </para>
///
/// <para>
/// A command that produced no recordable resource — a business CONFLICT such as a duplicate workspace slug or
/// an already-existing asset link — is signalled by a null from the caller's result-id selector. The key is
/// then NOT recorded (so a corrected retry under the same key can still succeed) and the command result is
/// returned verbatim for the endpoint to map to its own 4xx; there is no created row to roll back. The
/// idempotency scope is opaque and caller-composed (per tenant and per operation, e.g.
/// <c>session-create:{organizationId}</c>), so one tenant's keys never collide with another's (threat T5) and a
/// create key never collides with a reveal key.
/// </para>
/// </summary>
internal sealed class IdempotentResourceCreator
{
    private readonly IIdempotencyKeyStore _idempotency;
    private readonly TransactionalUnitOfWork _unitOfWork;

    public IdempotentResourceCreator(IIdempotencyKeyStore idempotency, TransactionalUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Returns the result id a prior request recorded for this (scope, key), or <see langword="null"/> when the
    /// key is absent/blank or has not been recorded yet. This is the fast-path replay probe a route runs BEFORE
    /// an irreversible pre-create side effect (for example a quota consume), so a replay performs neither the
    /// side effect nor the create.
    /// </summary>
    public async Task<Guid?> TryReplayAsync(string? idempotencyKey, string scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var existing = await _idempotency
            .FindAsync(scope, idempotencyKey.Trim(), cancellationToken)
            .ConfigureAwait(false);
        return existing?.ResultId;
    }

    /// <summary>
    /// Runs <paramref name="create"/> idempotently for the optional <paramref name="idempotencyKey"/>.
    /// </summary>
    /// <typeparam name="TResult">The command result the endpoint builds its response from (and from which
    /// <paramref name="resultIdSelector"/> extracts the produced resource id).</typeparam>
    /// <param name="idempotencyKey">The client idempotency key, or null/blank for no idempotency.</param>
    /// <param name="scope">The opaque, caller-composed idempotency scope (per tenant and operation).</param>
    /// <param name="now">The timestamp recorded on a new key.</param>
    /// <param name="create">Creates and persists the resource (and any in-transaction side effects) and returns
    /// the command result; it runs INSIDE the unit-of-work transaction.</param>
    /// <param name="resultIdSelector">Extracts the produced resource id to bind to the key, or null when the
    /// command produced no recordable resource (a business conflict), so the key is not recorded.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task<IdempotentCreation<TResult>> CreateAsync<TResult>(
        string? idempotencyKey,
        string scope,
        DateTimeOffset now,
        Func<CancellationToken, Task<TResult>> create,
        Func<TResult, Guid?> resultIdSelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(resultIdSelector);

        // No client idempotency key: run the create once in a unit of work; nothing to record or replay.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var unkeyed = await _unitOfWork.ExecuteAsync(create, cancellationToken).ConfigureAwait(false);
            return IdempotentCreation<TResult>.Created(unkeyed);
        }

        var key = idempotencyKey.Trim();

        // Fast path: the key was already recorded by a prior committed request -> replay its original result.
        var existing = await _idempotency.FindAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (existing is { ResultId: { } recordedResultId })
        {
            return IdempotentCreation<TResult>.Replay(recordedResultId);
        }

        // First time (as far as this request can see): run the create AND record the key bound to the produced
        // resource id, atomically, so a part-way failure can never leave a resource without its key or a key
        // without its resource.
        try
        {
            var result = await _unitOfWork
                .ExecuteAsync(
                    async transactionCancellationToken =>
                    {
                        var created = await create(transactionCancellationToken).ConfigureAwait(false);

                        // A command that produced no recordable resource (a business conflict) is committed
                        // WITHOUT recording the key, so a corrected retry under the same key can still succeed.
                        // There is no created row to roll back in that case.
                        if (resultIdSelector(created) is not { } producedId)
                        {
                            return created;
                        }

                        var record = IdempotencyKey.Create(scope, key, now, producedId);
                        var addResult = await _idempotency
                            .AddAsync(record, transactionCancellationToken)
                            .ConfigureAwait(false);
                        if (addResult == IdempotencyKeyAddResult.Duplicate)
                        {
                            // A concurrent first request committed the same (scope, key) first; ABANDON this
                            // attempt's newly created resource (the throw rolls the transaction back) so the key
                            // maps to exactly one resource, then replay the winner's result below.
                            throw new ReplayRaceException();
                        }

                        return created;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return IdempotentCreation<TResult>.Created(result);
        }
        catch (ReplayRaceException)
        {
            // The winner committed the key (and its result id); re-read and replay it.
            var winner = await _idempotency.FindAsync(scope, key, cancellationToken).ConfigureAwait(false);
            if (winner is { ResultId: { } winnerResultId })
            {
                return IdempotentCreation<TResult>.Replay(winnerResultId);
            }

            // Unreachable in practice: the duplicate insert proved a row exists with this (scope, key), and the
            // create scopes always bind a result id. Fail loudly rather than silently returning nothing.
            throw new InvalidOperationException(
                "The idempotency key was recorded concurrently but could not be re-read.");
        }
    }

    /// <summary>
    /// Internal control-flow signal that a concurrent first request won the unique (scope, key) race, so this
    /// attempt must roll its created resource back and replay the winner's result. Never surfaces to a caller.
    /// </summary>
    private sealed class ReplayRaceException : Exception
    {
    }
}

/// <summary>
/// The outcome of <see cref="IdempotentResourceCreator.CreateAsync{TResult}"/> (CORE-DX-004): either the
/// freshly created command <see cref="Result"/>, or a <see cref="Replayed"/> marker carrying the
/// <see cref="ReplayResultId"/> of the resource a prior request created (which the endpoint re-loads and
/// returns as the original result).
/// </summary>
internal readonly record struct IdempotentCreation<TResult>(
    bool Replayed,
    Guid ReplayResultId,
    TResult Result)
{
    /// <summary>A fresh create; <paramref name="result"/> is the command result (read only when not replayed).</summary>
    public static IdempotentCreation<TResult> Created(TResult result) => new(false, Guid.Empty, result);

    /// <summary>
    /// A replay; the caller re-loads the resource identified by <paramref name="resultId"/>. The unused
    /// <see cref="Result"/> is left at its default and must not be read on this path.
    /// </summary>
    public static IdempotentCreation<TResult> Replay(Guid resultId) => new(true, resultId, default!);
}
