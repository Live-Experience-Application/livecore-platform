// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Persistence contract for the append-only session event stream (CORE-RT-003). The Realtime module owns
/// the <c>session_events</c> table; other modules cause events to be appended only through the Realtime
/// publisher, never by writing this table directly (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations; docs/05_MODULE_CONTRACTS.md: the Realtime module owns "event delivery").
///
/// APPEND-ONLY. The contract exposes only an append and a tenant- and session-scoped read: there is NO
/// update and NO delete, because session events are immutable (docs/10_DATABASE_SCHEMA.md: "session
/// events are append-only"). Every read is scoped by BOTH the organization and the session, leading with
/// the tenant column, so one tenant's or one session's events are never returned through another's id
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1). There is no list-everything method and no
/// by-id-alone lookup.
/// </summary>
public interface ISessionEventRepository
{
    /// <summary>
    /// Appends an event to its session's stream. The append allocates and stamps the event's per-session,
    /// gap-free, strictly monotonic <see cref="SessionEvent.Sequence"/> (CORE-RTC-001) before persisting it,
    /// so ordering and replay use the sequence rather than the millisecond-resolution id. The event is
    /// immutable; this is the only write path. A foreign-key violation (a non-existent
    /// tenant/workspace/session) surfaces as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every event of the given session (owned by the given organization) in deterministic append
    /// order — by the per-session, gap-free, strictly monotonic <see cref="SessionEvent.Sequence"/>
    /// (CORE-RTC-001), which preserves the append order of events created within the same millisecond
    /// (unlike the millisecond-resolution UUIDv7 id) and is provider-independent. The list is tenant- AND
    /// session-scoped: the predicate leads with <c>organization_id</c> and matches <c>session_id</c>, so a
    /// foreign tenant's or session's events are NEVER returned even when their ids would otherwise be
    /// addressable (threat T5/T1). The <c>created_at</c> column still backs the time-range replay index
    /// (CORE-RT-005).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or session id is empty.</exception>
    Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
        Guid organizationId,
        Guid sessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists a BOUNDED, CURSORED slice of the given session's stream (CORE-PERF-002): the events whose
    /// per-session <see cref="SessionEvent.Sequence"/> is strictly greater than
    /// <paramref name="afterSequence"/> (or from the start when it is <see langword="null"/>), in append
    /// (sequence) order, capped at <paramref name="limit"/> rows. The cursor and the cap are pushed into
    /// SQL — a <c>WHERE sequence &gt; cursor ORDER BY sequence LIMIT limit</c> backed by the unique
    /// <c>session_events(session_id, sequence)</c> index — so a reconnect replay reads ONLY the rows after
    /// the client cursor up to the cap rather than loading the whole stream and filtering in memory; replay
    /// cost is bounded and does not grow with stream length (threat T9 abuse/DoS — a reconnect storm cannot
    /// load an unbounded stream). The slice is tenant- AND session-scoped exactly like
    /// <see cref="ListBySessionAsync"/> (the predicate leads with <c>organization_id</c> and matches
    /// <c>session_id</c>; threat T5/T1). Because the sequence is gap-free and monotonic, a cursor of N
    /// returns N+1.. with no skips or duplicates, and a full slice (exactly <paramref name="limit"/> rows)
    /// signals more may remain after its last sequence — the caller pages forward by that sequence.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or session id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is below 1.</exception>
    Task<IReadOnlyList<SessionEvent>> ListBySessionAfterAsync(
        Guid organizationId,
        Guid sessionId,
        long? afterSequence,
        int limit,
        CancellationToken cancellationToken);
}
