namespace LiveCore.Api.Realtime;

/// <summary>
/// The authorized FACTS of an admitted realtime hub connection (CORE-RTC-002): the tenant/workspace/session
/// it belongs to, the authenticated subject behind it, and — for a participant connection — the participant
/// record it owns. It is produced by <see cref="RealtimeConnectionResolver"/> at admission time (the same
/// place the SERVER-MANAGED groups are computed, from the same resolved <c>TenantContext</c>/session/
/// participant), travels on the <see cref="RealtimeConnectionAdmission"/>, and is recorded by the
/// <see cref="SessionHub"/> in the <see cref="RealtimeConnectionRegistry"/> so a later re-authorization can
/// find exactly the connections a removal or demotion affects.
///
/// It is the MATCH KEY for eviction (<see cref="IRealtimeConnectionEvictor"/>), never an authorization input
/// of its own: a connection is only ever recorded here AFTER the resolver admitted it, and eviction can only
/// ever REMOVE a connection, so this type can never widen an audience. The discriminator between the two
/// connection shapes is <see cref="ParticipantId"/>: a PARTICIPANT connection carries it (and is matched by
/// participant removal); a host/observer MEMBER connection leaves it <see langword="null"/> (and is matched
/// by a member role change). The ids are opaque surrogates only — no display name, token or resource content
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="OrganizationId">The tenant the connection was admitted within.</param>
/// <param name="WorkspaceId">The session's own workspace (where the caller's role/participant standing lives).</param>
/// <param name="SessionId">The session the connection joined.</param>
/// <param name="UserProfileId">The authenticated subject behind the connection (the caller's user profile id).</param>
/// <param name="ParticipantId">
/// The participant record a PARTICIPANT connection owns; <see langword="null"/> for a host/observer member
/// connection.
/// </param>
internal readonly record struct RealtimeConnectionSubject(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid SessionId,
    Guid UserProfileId,
    Guid? ParticipantId)
{
    /// <summary>
    /// Whether this is a PARTICIPANT connection (it owns a participant record). A member (host/observer)
    /// connection returns <see langword="false"/>.
    /// </summary>
    public bool IsParticipant => ParticipantId is not null;
}
