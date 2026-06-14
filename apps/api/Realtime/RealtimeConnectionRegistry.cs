using System.Collections.Concurrent;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Tracks the live realtime hub connections THIS API instance holds and re-authorizes them when a caller's
/// standing changes (CORE-RTC-002). It is the in-memory record the <see cref="SessionHub"/> writes on connect
/// (the admitted connection's <see cref="RealtimeConnectionSubject"/> + an abort handle) and clears on
/// disconnect, and the <see cref="IRealtimeConnectionEvictor"/> it implements reads to find exactly the
/// connections a participant removal or a member role change affects and abort them.
///
/// EVICTION ABORTS THE CONNECTION. Aborting the socket is the strongest form of "stops receiving events": the
/// connection is torn down, so no further group send can reach it, and when the client reconnects the
/// <see cref="RealtimeConnectionResolver"/> re-authorizes it from scratch (a demoted host re-joins only its
/// new role's groups; a removed participant is denied) — the single authorization path, reused. Eviction only
/// ever REMOVES a connection, so it can never widen an audience (threat T3 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// SINGLE-INSTANCE SCOPE. The registry holds only the connections of the instance it runs on (the abort handle
/// is an in-process <c>HubCallerContext.Abort</c>), so it evicts a connection on THIS instance immediately. In
/// a multi-instance deployment a connection on another instance is evicted when that instance handles the same
/// command, or — as the always-on backstop — by the per-event recipient computation that already re-gates the
/// participant audience fan-out; cross-instance host/observer eviction is a documented follow-up, the same
/// single-instance posture as the in-process backplane (docs/11_REALTIME_SYNC.md "Scale-out").
///
/// It is registered as a SINGLETON (it outlives any request) and is safe for concurrent access from hub
/// connect/disconnect callbacks and command threads (a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed
/// by connection id). An abort that throws (a connection already torn down) is swallowed so one dead
/// connection can never stop the rest of an eviction.
/// </summary>
internal sealed class RealtimeConnectionRegistry : IRealtimeConnectionEvictor
{
    // Keyed by SignalR connection id (unique per live connection). The value carries the connection's
    // authorized facts (the match key for eviction) and an in-process abort handle.
    private readonly ConcurrentDictionary<string, Entry> _connections = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of live connections currently tracked on this instance. A diagnostic accessor only — never
    /// an authorization input — exposed so a test can wait until the hub has finished admitting a connection
    /// before exercising an eviction. It mirrors the live realtime-connection metric gauge (CORE-OBS-001).
    /// </summary>
    internal int Count => _connections.Count;

    /// <summary>
    /// Records an admitted connection so a later re-authorization can find and evict it. Called by the hub
    /// AFTER the resolver admitted the connection and it joined its groups, with the admission's
    /// <paramref name="subject"/> and an <paramref name="abort"/> handle that tears the connection down
    /// (the connection's <c>HubCallerContext.Abort</c>). Re-registering the same connection id replaces the
    /// entry (a connection id is unique per live connection, so this is just last-writer-wins safety).
    /// </summary>
    /// <exception cref="ArgumentException">The connection id is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">The abort handle is null.</exception>
    public void Register(string connectionId, RealtimeConnectionSubject subject, Action abort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(abort);

        _connections[connectionId] = new Entry(subject, abort);
    }

    /// <summary>
    /// Forgets a connection (called by the hub on disconnect). Removing an unknown connection id is a safe
    /// no-op, so a connection aborted by an eviction (which also disconnects and unregisters) is cleared
    /// exactly once.
    /// </summary>
    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        _connections.TryRemove(connectionId, out _);
    }

    /// <inheritdoc />
    public Task EvictParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken)
    {
        // A participant connection is matched by the full tenant/workspace/session/participant tuple, so a
        // participant with the same id under another session/workspace/tenant (which cannot share a row but
        // guards the caller's intent) is never evicted (threats T1/T5). An empty id can never address a real
        // participant, so it matches nothing.
        if (participantId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        EvictWhere(entry =>
            entry.Subject.ParticipantId == participantId
            && entry.Subject.SessionId == sessionId
            && entry.Subject.WorkspaceId == workspaceId
            && entry.Subject.OrganizationId == organizationId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EvictWorkspaceMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // A member connection is matched by tenant + workspace + subject AND only when it is NOT a participant
        // connection (a membership role change does not affect the subject's separate participant standing).
        // An empty subject id can never address a real member, so it matches nothing.
        if (userProfileId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        EvictWhere(entry =>
            !entry.Subject.IsParticipant
            && entry.Subject.UserProfileId == userProfileId
            && entry.Subject.WorkspaceId == workspaceId
            && entry.Subject.OrganizationId == organizationId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Aborts and forgets every held connection matching <paramref name="predicate"/>. It snapshots the
    /// entries first (so the loop never races a concurrent register/disconnect mutating the dictionary) and
    /// removes each match before aborting, so the entry is gone even if the abort's own disconnect callback
    /// does not run on this instance. An abort that throws is swallowed: a single dead connection can never
    /// stop the rest of the eviction.
    /// </summary>
    private void EvictWhere(Func<Entry, bool> predicate)
    {
        foreach (var (connectionId, entry) in _connections.ToArray())
        {
            if (!predicate(entry))
            {
                continue;
            }

            _connections.TryRemove(connectionId, out _);

            try
            {
                entry.Abort();
            }
            catch
            {
                // The connection was already torn down (or the transport rejected the abort). It is no longer
                // receiving events, which is the whole point, so the failure is intentionally ignored.
            }
        }
    }

    private readonly record struct Entry(RealtimeConnectionSubject Subject, Action Abort);
}
