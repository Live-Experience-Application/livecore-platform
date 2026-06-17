// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Computes a reconnecting caller's recipient-safe replay of a session's durable event stream (CORE-RT-005,
/// "reconnect replay with filtering"). This is the "server-side replay filter" of docs/11_REALTIME_SYNC.md
/// and the "query events after last acknowledged event -&gt; filter each event through Visibility module
/// -&gt; send only projected recipient-safe payloads" steps of docs/09_EVENT_CATALOG.md. Its acceptance is
/// the epic's standing promise — "Realtime delivery never leaks hidden events" — extended to reconnect:
/// "reconnect replay filters events again" (threat T3 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE FILTER IS THE LIVE DELIVERY, REPLAYED. For each event after the acknowledged cursor, this service
/// re-runs the EXACT live recipient computation (<see cref="ISessionEventRecipientResolver"/>, CORE-RT-004)
/// and keeps only the delivery addressed to one of the caller's OWN server-managed groups
/// (<see cref="RealtimeGroups"/>) — the groups the caller would join on a live connection (resolved by
/// <see cref="RealtimeConnectionResolver"/>). Because the gating and the host-vs-audience projection are
/// REUSED from the resolver — not reimplemented — the replayed set and projection can never diverge from
/// what live delivery would have produced (docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic
/// elsewhere"; the Realtime module "may not send unfiltered events"). The visibility is re-evaluated at
/// REPLAY time against the current rules, exactly as docs/09 step 5 requires ("filter each event through
/// Visibility module" on reconnect).
///
/// Consequently each recipient class replays exactly its live view: a host replays every event with the
/// host projection (the routing target included); an observer replays only audience-wide events the
/// audience may currently see; a participant replays the audience-wide events they may see plus the
/// private events targeted at THEM, and NEVER a private event targeted at another participant (a
/// non-selected participant is in neither that event's recipient groups — the crown jewel, threat T3).
///
/// THE CURSOR is the caller's last acknowledged per-session SEQUENCE number (CORE-RTC-001;
/// docs/11_REALTIME_SYNC.md). Events are replayed strictly AFTER it in append (sequence) order — the stream
/// is read by the gap-free monotonic sequence the repository orders by, NOT the millisecond-resolution
/// UUIDv7 id — so a cursor of N returns exactly N+1.. with no skips or duplicates. A cursor below the first
/// sequence replays from the start — every event is still re-filtered per recipient (so nothing leaks)
/// and the client deduplicates already-seen events (docs/11_REALTIME_SYNC.md requires client-side
/// "duplicate event handling"), so a stale or out-of-range cursor is fail-safe and never silently drops
/// unacknowledged events.
///
/// BOUNDED AND CURSORED (CORE-PERF-002). The cursor AND a row cap (<see cref="MaxReplayEvents"/>) are pushed
/// into SQL (<see cref="ISessionEventRepository.ListBySessionAfterAsync"/>): the replay reads ONLY the
/// post-cursor rows up to the cap — a <c>WHERE sequence &gt; cursor ORDER BY sequence LIMIT cap</c> backed by
/// the unique <c>session_events(session_id, sequence)</c> index — rather than loading the whole stream and
/// filtering in memory, so a reconnect storm cannot become a self-inflicted DoS (threat T9). The recipient
/// visibility for the WHOLE page is then resolved with a SINGLE batched lookup
/// (<see cref="ISessionEventRecipientResolver.ResolveBatchAsync"/>, reusing the CORE-PERF-001 in-memory
/// audience gate) rather than one query per event, so replay cost is bounded and does not grow with events
/// times participants. When a page comes back FULL the service hands back the page's highest RAW sequence as
/// <see cref="SessionReplaySlice.NextSequence"/> so the client pages forward (even across a full page with no
/// event it may see) — the cap never silently drops unacknowledged events.
///
/// This is a plain decision service over already-resolved ids (mirroring <c>RevealService</c> and
/// <c>VisibilityPolicy</c>): the tenant boundary, session existence and the caller's relationship are
/// resolved by the endpoint (the same tenant check and connection resolver the live hub uses) before this
/// runs; it trusts those resolved ids and the resolved group set, and decides only the per-event
/// filtering.
/// </summary>
internal sealed class SessionReplayService
{
    /// <summary>
    /// The maximum number of raw stream rows a single replay page reads (CORE-PERF-002). It is the SQL row
    /// cap pushed into <see cref="ISessionEventRepository.ListBySessionAfterAsync"/>, so a reconnect can never
    /// load an unbounded stream (threat T9). A client with a larger backlog pages forward by
    /// <see cref="SessionReplaySlice.NextSequence"/>.
    /// </summary>
    internal const int MaxReplayEvents = 500;

    private readonly ISessionEventRepository _events;
    private readonly ISessionEventRecipientResolver _recipients;

    public SessionReplayService(
        ISessionEventRepository events,
        ISessionEventRecipientResolver recipients)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(recipients);
        _events = events;
        _recipients = recipients;
    }

    /// <summary>
    /// Replays, for the caller identified by their server-managed <paramref name="recipientGroups"/>, the
    /// recipient-safe events of the given session (owned by the given tenant) whose per-session
    /// <see cref="SessionEvent.Sequence"/> is strictly greater than <paramref name="afterSequence"/>. Only
    /// deliveries addressed to one of the caller's groups are kept, each carrying the projection (host vs
    /// audience) that group's recipients may see, so the result never contains an event the caller may not
    /// receive (threat T3).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the session (the read leads with this; threat T5).</param>
    /// <param name="sessionId">The session whose append-only stream is replayed.</param>
    /// <param name="recipientGroups">
    /// The caller's server-computed groups for this session (from <see cref="RealtimeConnectionResolver"/>);
    /// the client never supplies group names.
    /// </param>
    /// <param name="afterSequence">
    /// The caller's last acknowledged sequence number, or <see langword="null"/> to replay from the start.
    /// Events with a greater sequence are returned, so the cursor is gap-aware: a client that has processed
    /// up to sequence N receives N+1.. with no skips or duplicates (CORE-RTC-001).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One BOUNDED page of the recipient-safe envelopes the caller is entitled to, in append (sequence)
    /// order, plus the forward cursor to the next page when one exists (<see cref="SessionReplaySlice"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException">The group set is null.</exception>
    /// <exception cref="ArgumentException">The organization id or session id is empty.</exception>
    public async Task<SessionReplaySlice> ReplayAsync(
        Guid organizationId,
        Guid sessionId,
        IReadOnlyCollection<string> recipientGroups,
        long? afterSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipientGroups);

        // Empty ids can never address a stored session's stream, so fail fast rather than scanning an
        // arbitrary set of rows (mirrors the repository's empty-id guards).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        // An admitted caller always joins at least one group; with no groups, no delivery can ever match,
        // so the replay is legitimately empty (fail-closed) without touching the database.
        if (recipientGroups.Count == 0)
        {
            return SessionReplaySlice.Empty;
        }

        var groups = new HashSet<string>(recipientGroups, StringComparer.Ordinal);

        // BOUNDED + CURSORED read (CORE-PERF-002): the cursor and the cap are pushed into SQL, so this reads
        // ONLY the post-cursor rows up to the cap (ordered by the gap-free monotonic sequence; threat T5/T1),
        // never the whole stream loaded then filtered in memory (threat T9 abuse/DoS).
        var page = await _events
            .ListBySessionAfterAsync(organizationId, sessionId, afterSequence, MaxReplayEvents, cancellationToken)
            .ConfigureAwait(false);

        // Re-run the LIVE recipient computation for the WHOLE page in ONE batched lookup (CORE-PERF-002,
        // reusing CORE-PERF-001) rather than one query per event. Reusing the resolver — not a parallel
        // filter — guarantees the replayed projection is identical to the live one (threat T3; docs/09 step 5).
        var deliveriesPerEvent = await _recipients
            .ResolveBatchAsync(page, cancellationToken)
            .ConfigureAwait(false);

        var replay = new List<SessionEventEnvelope>(page.Count);
        for (var index = 0; index < page.Count; index++)
        {
            foreach (var delivery in deliveriesPerEvent[index])
            {
                if (groups.Contains(delivery.Group))
                {
                    replay.Add(delivery.Envelope);

                    // A single caller is addressed by at most one of an event's recipient groups (hosts
                    // XOR observers XOR their own participant group), so the first match is the caller's
                    // projection.
                    break;
                }
            }
        }

        // A FULL page means more rows MAY exist after it: hand back the page's highest RAW sequence as the
        // next cursor so the client pages forward even across a full page with no event it may see (the cap
        // never silently drops unacknowledged events). A non-full page means the caller has caught up.
        var nextSequence = page.Count == MaxReplayEvents ? page[^1].Sequence : (long?)null;

        return new SessionReplaySlice(replay, nextSequence);
    }
}
