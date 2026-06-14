namespace LiveCore.Api.Realtime;

/// <summary>
/// Re-authorizes live realtime connections when a caller's standing changes mid-session (CORE-RTC-002) — the
/// EVICTION seam. A connection's server-managed groups are resolved ONCE at connect
/// (<see cref="RealtimeConnectionResolver"/> / <see cref="SessionHub"/>), so without this seam a connection
/// keeps its groups until it reconnects: a removed participant's still-open socket stays in its participant
/// group and a demoted host keeps receiving host/observer group sends (the participant audience fan-out is
/// re-gated per event by <see cref="SessionEventRecipientResolver"/>, but group MEMBERSHIP is not). This is
/// the signal a removal/role-change command raises so the affected connections stop receiving events they are
/// no longer authorized to see WITHOUT waiting for a client-initiated reconnect
/// (docs/11_REALTIME_SYNC.md; threat T3 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// It only ever EVICTS (aborts) a connection — it never adds one to a group and never sends an event — so it
/// can never widen an audience. The authoritative re-admission is the existing resolver: an evicted client
/// that reconnects is re-authorized from scratch, so a demoted host re-joins only the groups its new role
/// allows and a removed participant is denied (fail-closed). This is the same single authorization path,
/// reused — not a parallel one (docs/05_MODULE_CONTRACTS.md: the Visibility/authorization decision is not
/// duplicated).
///
/// It is a PUBLIC seam because the lifecycle commands that raise it live in other modules (the Sessions
/// module's participant-leave/remove flow; a workspace/organization member role-change command). The methods
/// take only opaque surrogate ids, so no internal type or content crosses the seam (threat T7).
/// </summary>
public interface IRealtimeConnectionEvictor
{
    /// <summary>
    /// Evicts every live connection a participant owns in the given session, so the participant's still-open
    /// socket stops receiving events the instant they are removed (or leave), not only when it reconnects.
    /// The match is scoped by the full tenant/workspace/session/participant tuple — a participant in another
    /// session, workspace or tenant is never touched (threats T1/T5). Aborting a connection that is no longer
    /// live (or that this instance does not hold) is a safe no-op. Returns once the held connections have been
    /// signalled to abort.
    /// </summary>
    Task EvictParticipantAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evicts every live host/observer MEMBER connection the given subject holds in the given workspace, so a
    /// member whose workspace role changed (in particular a demoted host) stops receiving the host/observer
    /// group deliveries their old role allowed, not only when they reconnect. Only MEMBER connections are
    /// affected; a participant connection the same subject may also hold is NOT evicted (their participant
    /// standing is unchanged by a membership role change). The match is scoped by tenant + workspace + subject,
    /// so a member of another workspace or tenant is never touched (threats T1/T5). Aborting a connection that
    /// is no longer live (or that this instance does not hold) is a safe no-op.
    /// </summary>
    Task EvictWorkspaceMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken);
}
