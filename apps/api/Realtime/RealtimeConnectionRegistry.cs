// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
/// CROSS-INSTANCE PROPAGATION (CORE-RES-008). The abort handle is an in-process <c>HubCallerContext.Abort</c>, so
/// each instance can only abort the connections IT holds — without propagation a demoted/removed user keeps a live
/// socket on another replica until it reconnects. So after evicting its OWN held connections (always first and
/// unconditionally), the registry PUBLISHES an opaque eviction descriptor (<see cref="RealtimeConnectionEviction"/>)
/// over the configured <see cref="IRealtimeConnectionEvictionBackplane"/> — the same Valkey/Redis backplane the
/// realtime fan-out uses — and every replica's <see cref="RealtimeConnectionEvictionListener"/> feeds received
/// descriptors into <see cref="ApplyRemoteEviction"/>, so the socket is aborted on every replica within a bounded
/// window. The broadcast is best-effort and can only ever cause MORE eviction (never less), so a dropped message
/// merely falls back to the pre-CORE-RES-008 reconnect window and never widens an audience (no new fail-open path).
/// A received eviction is applied LOCALLY ONLY and never re-published, so it cannot echo across the backplane. With
/// no backplane configured the <see cref="NullRealtimeConnectionEvictionBackplane"/> no-op keeps single-instance
/// behaviour unchanged (docs/11_REALTIME_SYNC.md "Connection re-authorization and eviction"). It is the
/// realtime-connection counterpart of the authorization-cache invalidation backplane (CORE-RES-007).
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

    // The cross-instance propagation seam (CORE-RES-008). The no-op default means a registry constructed without a
    // backplane (single-instance / unit tests) behaves exactly as before: local eviction only, nothing broadcast.
    private readonly IRealtimeConnectionEvictionBackplane _backplane;

    /// <summary>
    /// Creates the registry over the given cross-instance eviction <paramref name="backplane"/>. A null backplane
    /// (the single-instance default, and the unit-test default) uses the <see cref="NullRealtimeConnectionEvictionBackplane"/>
    /// no-op, so eviction stays local-only and nothing is broadcast.
    /// </summary>
    public RealtimeConnectionRegistry(IRealtimeConnectionEvictionBackplane? backplane = null)
        => _backplane = backplane ?? NullRealtimeConnectionEvictionBackplane.Instance;

    /// <summary>
    /// The number of live connections currently tracked on this instance. A diagnostic accessor only — never
    /// an authorization input — exposed so a test can wait until the hub has finished admitting a connection
    /// before exercising an eviction. It mirrors the live realtime-connection metric gauge (CORE-OBS-001).
    /// </summary>
    internal int Count => _connections.Count;

    /// <summary>
    /// The set of participant ids that currently hold at least one live PARTICIPANT connection to the given
    /// session on THIS API instance — the presence input to the participant roster + presence read
    /// (CORE-PRS-002, the "Vertical Adopter Consumability Completeness" epic). It snapshots the tracked
    /// connections and returns the distinct participant ids whose connection matches the full
    /// tenant/workspace/session tuple and carries a participant id, so a host/member roster read can mark
    /// exactly those participants present.
    ///
    /// SCOPED EXACTLY like eviction (<see cref="EvictParticipantLocal"/>): a connection matches only when its
    /// organization, workspace AND session all equal the arguments, so a participant connected to another
    /// session, workspace or tenant is never reported present here (threats T1/T5 in
    /// docs/07_SECURITY_THREAT_MODEL.md). A host/observer MEMBER connection carries no participant id
    /// (<see cref="RealtimeConnectionSubject.ParticipantId"/> is null) and is excluded — presence here is the
    /// PARTICIPANT audience, the roster's population. An empty boundary id can never address a real session, so
    /// it matches nothing and returns the empty set rather than matching loosely.
    ///
    /// THIS-INSTANCE ONLY. The registry records the connections THIS instance holds (the same per-instance
    /// record eviction acts on); the SignalR scale-out backplane transports group SENDS across instances but
    /// does not aggregate a global connection list, so the returned set reflects presence on this instance.
    /// Aggregating presence across every replica would need a shared presence store and is a documented
    /// follow-up (docs/11_REALTIME_SYNC.md "Participant roster and presence read"). It never widens
    /// authorization — a participant connected only to another replica simply reads as not-present, never more
    /// visible — so it is a fail-safe under-report, exactly like the single-instance realtime delivery
    /// constraint.
    /// </summary>
    public IReadOnlySet<Guid> GetConnectedParticipantIds(Guid organizationId, Guid workspaceId, Guid sessionId)
    {
        var present = new HashSet<Guid>();

        // An empty boundary can never address a real session; return the empty set rather than match loosely.
        if (organizationId == Guid.Empty || workspaceId == Guid.Empty || sessionId == Guid.Empty)
        {
            return present;
        }

        // Snapshot first (a ToArray of the entries) so the scan never races a concurrent register/disconnect
        // mutating the dictionary, mirroring EvictWhere.
        foreach (var (_, entry) in _connections.ToArray())
        {
            var subject = entry.Subject;
            if (subject.ParticipantId is { } participantId
                && subject.SessionId == sessionId
                && subject.WorkspaceId == workspaceId
                && subject.OrganizationId == organizationId)
            {
                present.Add(participantId);
            }
        }

        return present;
    }

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
        // An empty id can never address a real participant on any replica, so it matches nothing and is not
        // broadcast (a peer would only reject it defensively anyway).
        if (participantId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        // Evict the sockets THIS instance holds first and unconditionally, then broadcast best-effort so a socket
        // the same participant holds on another replica is aborted too (CORE-RES-008). A failed/dropped broadcast
        // only falls back to the pre-CORE-RES-008 reconnect window — never a widened audience.
        EvictParticipantLocal(organizationId, workspaceId, sessionId, participantId);
        _backplane.Publish(
            RealtimeConnectionEviction.ForParticipant(organizationId, workspaceId, sessionId, participantId).Serialize());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EvictWorkspaceMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // An empty subject id can never address a real member on any replica, so it matches nothing and is not
        // broadcast.
        if (userProfileId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        // Local eviction first and unconditionally, then best-effort cross-instance propagation (CORE-RES-008): a
        // demoted member's host/observer socket held by another replica is aborted there too.
        EvictWorkspaceMemberLocal(organizationId, workspaceId, userProfileId);
        _backplane.Publish(
            RealtimeConnectionEviction.ForWorkspaceMember(organizationId, workspaceId, userProfileId).Serialize());

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies an eviction a PEER replica broadcast (CORE-RES-008): aborts only the matching connections THIS
    /// instance holds. It deliberately does NOT re-publish (that would echo forever across the backplane), and it
    /// ignores a malformed or unrecognized token defensively, so a stray message can never abort an unintended
    /// connection. Called by the <see cref="RealtimeConnectionEvictionListener"/> on every message received from the
    /// backplane.
    /// </summary>
    public void ApplyRemoteEviction(string evictionToken)
    {
        if (!RealtimeConnectionEviction.TryParse(evictionToken, out var eviction))
        {
            return;
        }

        // Local-only: do NOT publish, or a received eviction would echo back across the backplane forever.
        switch (eviction.Kind)
        {
            case RealtimeConnectionEvictionKind.Participant:
                EvictParticipantLocal(
                    eviction.OrganizationId, eviction.WorkspaceId, eviction.SessionId, eviction.SubjectId);
                break;
            case RealtimeConnectionEvictionKind.WorkspaceMember:
                EvictWorkspaceMemberLocal(eviction.OrganizationId, eviction.WorkspaceId, eviction.SubjectId);
                break;
        }
    }

    // Aborts the participant's connections held by THIS instance. A participant connection is matched by the full
    // tenant/workspace/session/participant tuple, so a participant with the same id under another
    // session/workspace/tenant is never evicted (threats T1/T5). An empty id matches nothing.
    private void EvictParticipantLocal(Guid organizationId, Guid workspaceId, Guid sessionId, Guid participantId)
    {
        if (participantId == Guid.Empty)
        {
            return;
        }

        EvictWhere(entry =>
            entry.Subject.ParticipantId == participantId
            && entry.Subject.SessionId == sessionId
            && entry.Subject.WorkspaceId == workspaceId
            && entry.Subject.OrganizationId == organizationId);
    }

    // Aborts the member's host/observer connections held by THIS instance. Matched by tenant + workspace + subject
    // AND only when NOT a participant connection (a membership role change does not affect the subject's separate
    // participant standing). An empty subject id matches nothing.
    private void EvictWorkspaceMemberLocal(Guid organizationId, Guid workspaceId, Guid userProfileId)
    {
        if (userProfileId == Guid.Empty)
        {
            return;
        }

        EvictWhere(entry =>
            !entry.Subject.IsParticipant
            && entry.Subject.UserProfileId == userProfileId
            && entry.Subject.WorkspaceId == workspaceId
            && entry.Subject.OrganizationId == organizationId);
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
