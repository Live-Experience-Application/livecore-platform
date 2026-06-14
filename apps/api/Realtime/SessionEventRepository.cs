using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Realtime;

/// <summary>
/// EF Core implementation of <see cref="ISessionEventRepository"/> (CORE-RT-003), backed by the
/// append-only <c>session_events</c> table mapped in <see cref="SessionEventConfiguration"/>. There is no
/// update or delete path: session events are immutable once appended.
/// </summary>
internal sealed class SessionEventRepository : ISessionEventRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public SessionEventRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        // Allocate the per-session, gap-free, strictly monotonic sequence (CORE-RTC-001) and stamp it on the
        // event BEFORE the insert, so ordering and replay use the sequence rather than the
        // millisecond-resolution UUIDv7 id. The allocation runs on the SAME context as the insert, so for the
        // transactional commands (reveal/hide, session lifecycle — CORE-CONC-002) it commits or rolls back
        // atomically with the event row: a rollback reclaims the number and the stream stays gap-free.
        var sequence = await AllocateNextSequenceAsync(sessionEvent.SessionId, cancellationToken).ConfigureAwait(false);
        sessionEvent.AssignSequence(sequence);

        _dbContext.SessionEvents.Add(sessionEvent);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Allocates the next per-session sequence number from the <c>session_event_sequences</c> counter
    /// (CORE-RTC-001). The increment is a SINGLE atomic <c>INSERT ... ON CONFLICT DO UPDATE</c>: it creates
    /// the counter at 1 on a session's first event, or increments it by exactly one otherwise. The
    /// conflict-path UPDATE takes a row lock on the counter, so two concurrent appends to the SAME session
    /// serialize — the second blocks until the first commits and then increments from the committed value,
    /// rather than both reading the same maximum and colliding — yielding a gap-free, strictly monotonic
    /// sequence even under a race. The just-allocated value is read back on the same context/transaction
    /// (it sees its own write), so it reflects this append's number.
    /// </summary>
    private async Task<long> AllocateNextSequenceAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // The same provider-portable upsert pattern the atomic quota consume uses (works on PostgreSQL and on
        // the SQLite test provider); every value is a parameter. session_id is the counter's primary key, so
        // it is the conflict target.
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO session_event_sequences (session_id, last_sequence)
               VALUES ({sessionId}, 1)
               ON CONFLICT (session_id)
               DO UPDATE SET last_sequence = session_event_sequences.last_sequence + 1",
            cancellationToken).ConfigureAwait(false);

        // Read the value this append just allocated. AsNoTracking forces a fresh read of the counter row
        // (never a stale tracked snapshot); within the command's transaction it sees this transaction's own
        // increment.
        return await _dbContext.SessionEventSequences
            .AsNoTracking()
            .Where(counter => counter.SessionId == sessionId)
            .Select(counter => counter.LastSequence)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
        Guid organizationId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored session's events, so the lookup fails fast instead of
        // returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        // The predicate leads with the tenant column and then matches the session, so the list is
        // exactly tenant- and session-scoped (threat T5/T1). The order is by the per-session monotonic
        // SEQUENCE (CORE-RTC-001) — gap-free append order that, unlike the millisecond-resolution UUIDv7 id,
        // preserves the order of events appended within the same millisecond — and is provider-independent.
        return await _dbContext.SessionEvents
            .Where(sessionEvent => sessionEvent.OrganizationId == organizationId
                && sessionEvent.SessionId == sessionId)
            .OrderBy(sessionEvent => sessionEvent.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
