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
/// THE CURSOR is the caller's "last acknowledged event id" (docs/11_REALTIME_SYNC.md). Events are replayed
/// strictly AFTER it in append order (the stream is read in the time-ordered surrogate-id order the
/// repository already guarantees). An unknown cursor replays the whole stream — every event is still
/// re-filtered per recipient (so nothing leaks) and the client deduplicates already-seen events
/// (docs/11_REALTIME_SYNC.md requires client-side "duplicate event handling"), so a stale or garbage
/// cursor is fail-safe and never silently drops unacknowledged events.
///
/// This is a plain decision service over already-resolved ids (mirroring <c>RevealService</c> and
/// <c>VisibilityPolicy</c>): the tenant boundary, session existence and the caller's relationship are
/// resolved by the endpoint (the same tenant check and connection resolver the live hub uses) before this
/// runs; it trusts those resolved ids and the resolved group set, and decides only the per-event
/// filtering.
/// </summary>
internal sealed class SessionReplayService
{
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
    /// recipient-safe events of the given session (owned by the given tenant) that occur after
    /// <paramref name="afterEventId"/>. Only deliveries addressed to one of the caller's groups are kept,
    /// each carrying the projection (host vs audience) that group's recipients may see, so the result
    /// never contains an event the caller may not receive (threat T3).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the session (the read leads with this; threat T5).</param>
    /// <param name="sessionId">The session whose append-only stream is replayed.</param>
    /// <param name="recipientGroups">
    /// The caller's server-computed groups for this session (from <see cref="RealtimeConnectionResolver"/>);
    /// the client never supplies group names.
    /// </param>
    /// <param name="afterEventId">The caller's last acknowledged event id, or <see langword="null"/> to replay from the start.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recipient-safe envelopes the caller is entitled to, in append order.</returns>
    /// <exception cref="ArgumentNullException">The group set is null.</exception>
    /// <exception cref="ArgumentException">The organization id or session id is empty.</exception>
    public async Task<IReadOnlyList<SessionEventEnvelope>> ReplayAsync(
        Guid organizationId,
        Guid sessionId,
        IReadOnlyCollection<string> recipientGroups,
        Guid? afterEventId,
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
            return [];
        }

        var groups = new HashSet<string>(recipientGroups, StringComparer.Ordinal);

        // The tenant- and session-scoped, append-ordered stream (the repository leads with the tenant
        // column and orders by the time-ordered surrogate id; threat T5/T1).
        var stream = await _events
            .ListBySessionAsync(organizationId, sessionId, cancellationToken)
            .ConfigureAwait(false);

        var pending = SliceAfter(stream, afterEventId);

        var replay = new List<SessionEventEnvelope>();
        foreach (var sessionEvent in pending)
        {
            // Re-run the LIVE recipient computation (CORE-RT-004) and keep only the delivery addressed to
            // one of the caller's own groups. Reusing the resolver — not a parallel filter — guarantees the
            // replayed projection is identical to the live one (threat T3; docs/09 step 5).
            var deliveries = await _recipients
                .ResolveAsync(sessionEvent, cancellationToken)
                .ConfigureAwait(false);
            foreach (var delivery in deliveries)
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

        return replay;
    }

    /// <summary>
    /// Returns the suffix of <paramref name="ordered"/> strictly AFTER the acknowledged
    /// <paramref name="afterEventId"/>, in append order. With no cursor the whole stream is returned; with
    /// a cursor not present in the stream the whole stream is returned too (fail-safe — see the type
    /// summary). The slice is positional within the already-ordered stream, so it is provider-independent
    /// and consistent with the repository's append ordering.
    /// </summary>
    private static IReadOnlyList<SessionEvent> SliceAfter(IReadOnlyList<SessionEvent> ordered, Guid? afterEventId)
    {
        if (afterEventId is not { } cursor)
        {
            return ordered;
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Id == cursor)
            {
                var tail = new List<SessionEvent>(ordered.Count - index - 1);
                for (var next = index + 1; next < ordered.Count; next++)
                {
                    tail.Add(ordered[next]);
                }

                return tail;
            }
        }

        return ordered;
    }
}
