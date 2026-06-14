using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Runs a multi-step command handler as ONE database transaction — a unit of work spanning a whole
/// handler (CORE-CONC-002). The reveal/hide and session start/end/cancel commands each change a
/// rule/state row, append an audit fact, change a quota counter and append a durable session event;
/// before this each of those was its own <c>SaveChangesAsync</c>, so a crash between steps could leave
/// visibility changed with no <c>ContentRevealed</c> event (replay would reconstruct a state the
/// append-only stream never recorded) or, on a retry, a double audit/event. Wrapping the writes in one
/// transaction makes the command atomic: every effect commits together or a part-way failure rolls the
/// lot back, so the append-only event stream can never diverge from persisted state.
///
/// <para>
/// The transaction is run INSIDE
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy"/>'s <c>ExecuteAsync</c> — the
/// strategy created by <c>Database.CreateExecutionStrategy()</c> — rather than around a bare
/// user-initiated <c>BeginTransaction</c>. This is the documented way to combine an explicit transaction
/// with an execution strategy: a RETRYING strategy forbids a user transaction opened outside its
/// <c>ExecuteAsync</c> (it could not re-run the work after a transient failure), so opening the
/// transaction inside the delegate keeps the command correct once CORE-CONC-003 turns on
/// <c>EnableRetryOnFailure</c> — and it is equally correct under today's default NON-retrying strategy,
/// which simply runs the delegate once. Because the whole delegate is the retry unit, every effect inside
/// it must be safe to re-run from scratch (the reveal command's idempotency-key write and the state-machine
/// guards already are); a retry begins a fresh transaction.
/// </para>
///
/// <para>
/// Every repository the handler calls writes through the SAME scoped <see cref="LiveCoreDbContext"/> this
/// unit of work begins the transaction on, so each repository's <c>SaveChangesAsync</c> enrols in this
/// transaction instead of committing independently. Reads, the in-memory state-machine guards and the
/// realtime DELIVERY stay OUTSIDE: a command computes its decision and opens the transaction only for the
/// writes, then COMMITS before handing the durable events to the realtime transport (commit-then-publish,
/// see <see cref="LiveCore.Api.Realtime.ISessionEventPublisher"/>), so a delivery failure can never roll
/// back already-committed state.
/// </para>
/// </summary>
internal sealed class TransactionalUnitOfWork
{
    private readonly LiveCoreDbContext _dbContext;

    public TransactionalUnitOfWork(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside one database transaction and returns its result. The work runs
    /// through the shared <see cref="LiveCoreDbContext"/>, so each repository <c>SaveChangesAsync</c> it
    /// performs participates in the same transaction; the transaction COMMITS only if the work completes,
    /// and any exception rolls every write back (the transaction is disposed without a commit). The
    /// transaction is begun and committed inside the execution strategy's <c>ExecuteAsync</c>, so it stays
    /// correct under a retrying strategy.
    /// </summary>
    /// <typeparam name="TResult">The value the work produces (for example the durable events to deliver after commit).</typeparam>
    /// <param name="work">The handler writes to commit atomically; it receives the cancellation token for the attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The work delegate is null.</exception>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy
            .ExecuteAsync(
                async executeCancellationToken =>
                {
                    // Open the transaction INSIDE the strategy's delegate (never a bare user-initiated
                    // BeginTransaction), so a retrying strategy can re-run the whole unit of work on a
                    // transient failure. await using disposes (and so ROLLS BACK) the transaction if the
                    // work throws before the commit.
                    await using var transaction = await _dbContext.Database
                        .BeginTransactionAsync(executeCancellationToken)
                        .ConfigureAwait(false);

                    var result = await work(executeCancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(executeCancellationToken).ConfigureAwait(false);
                    return result;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
