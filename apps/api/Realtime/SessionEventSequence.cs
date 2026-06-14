namespace LiveCore.Api.Realtime;

/// <summary>
/// The per-session allocator counter that hands out the gap-free, strictly monotonic event sequence numbers
/// stamped onto every <see cref="SessionEvent"/> (CORE-RTC-001). The Realtime module owns the
/// <c>session_event_sequences</c> table — one row per session holding the <see cref="LastSequence"/> handed
/// out so far — alongside the append-only <c>session_events</c> stream it feeds.
///
/// WHY A COUNTER ROW (not <c>MAX(sequence)+1</c> nor the UUIDv7 id). The id is only monotonic at millisecond
/// resolution, so events appended within one millisecond (a single reveal publishes several at the same
/// <c>now</c>) reorder under an id-ordered read. Allocating the next number from a counter row that the
/// database increments atomically (<see cref="SessionEventRepository.AppendAsync"/> issues a single
/// <c>INSERT ... ON CONFLICT DO UPDATE</c>) takes a row lock that SERIALIZES concurrent appends to the same
/// session: a second concurrent append blocks on the locked counter and then increments from the committed
/// value rather than colliding, so the per-session sequence is gap-free and strictly monotonic even under a
/// race. Because the increment runs in the command's unit-of-work transaction (CORE-CONC-002) together with
/// the event insert, a rollback reclaims the number — there is no gap.
///
/// The row is created lazily on a session's FIRST appended event and is keyed by <see cref="SessionId"/>
/// (a foreign key into <c>sessions</c> that CASCADES on delete, mirroring the session event stream): when a
/// session is removed its counter goes with it. There is no CLR write API: the counter is advanced only by
/// the atomic upsert in the append path, never by mutating a tracked instance, so it can never be set to an
/// arbitrary value.
/// </summary>
internal sealed class SessionEventSequence
{
    /// <summary>Materialization constructor for the persistence layer (the row is created by the append-path upsert).</summary>
    private SessionEventSequence()
    {
    }

    /// <summary>The session whose stream this counter allocates for (the <c>session_id</c> primary key and foreign key).</summary>
    public Guid SessionId { get; private set; }

    /// <summary>
    /// The highest sequence number handed out for the session so far. The next appended event takes
    /// <see cref="LastSequence"/> + 1; the first event of a session takes 1.
    /// </summary>
    public long LastSequence { get; private set; }
}
