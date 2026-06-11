namespace LiveCore.Api.Sessions;

/// <summary>
/// Outcome of deciding whether a participant may join a session
/// (CORE-SES-003): either an admission carrying the validated, tenant- and
/// workspace-scoped (organization, workspace, session, participant) identifiers, or
/// a typed fail-closed denial, never a partial admission (mirrors the fail-closed
/// result of CORE-ID-005's <c>TenantContextResolutionResult</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// This is an in-memory decision type only. It persists nothing and computes no
/// realtime concern: the durable <c>ParticipantJoined</c> session event, the
/// SignalR group assignment (docs/11_REALTIME_SYNC.md) and the participant
/// connection metadata are later stories (the Realtime epic and Participants-owned
/// work), not this one. An admission is therefore the security decision "this
/// participant may join this session", not a record that it has joined.
///
/// The admitted identifiers are exposed only when <see cref="Admitted"/> is true.
/// They are surrogate ids and an authorization reason only, so the whole type is
/// safe for structured logs (threat T7): there is no free-form metadata to leak.
/// </summary>
public sealed class SessionJoinResult
{
    private SessionJoinResult(
        bool admitted,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId,
        SessionJoinDenialReason reason)
    {
        Admitted = admitted;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SessionId = sessionId;
        ParticipantId = participantId;
        Reason = reason;
    }

    /// <summary>
    /// Whether the participant was admitted to the session. When <see langword="true"/>
    /// the admitted identifiers are populated and <see cref="Reason"/> is
    /// <see cref="SessionJoinDenialReason.None"/>; when <see langword="false"/> the
    /// identifiers are empty and <see cref="Reason"/> carries the specific denial.
    /// </summary>
    public bool Admitted { get; }

    /// <summary>
    /// Tenant boundary of the admitted join (the organization the session and
    /// participant both belong to). <see cref="Guid.Empty"/> on a denial.
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the admitted join (the workspace the session and
    /// participant both belong to). <see cref="Guid.Empty"/> on a denial.
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Surrogate id of the session the participant was admitted to.
    /// <see cref="Guid.Empty"/> on a denial.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>
    /// Surrogate id of the admitted participant. <see cref="Guid.Empty"/> on a
    /// denial.
    /// </summary>
    public Guid ParticipantId { get; }

    /// <summary>
    /// Why admission was denied; <see cref="SessionJoinDenialReason.None"/> on an
    /// admission.
    /// </summary>
    public SessionJoinDenialReason Reason { get; }

    /// <summary>
    /// Builds an admission for the validated join. Internal so only
    /// <see cref="SessionParticipantJoinService"/> can mint one, and only after the
    /// session and participant have both been loaded inside the (organization,
    /// workspace) boundary and re-validated, so an admission can never carry foreign
    /// data (threat T5). All four ids must be non-empty: an empty id can never
    /// identify a real admitted resource.
    /// </summary>
    /// <exception cref="ArgumentException">Any identifier is empty.</exception>
    internal static SessionJoinResult Admit(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (participantId == Guid.Empty)
        {
            throw new ArgumentException("Participant id must not be empty.", nameof(participantId));
        }

        return new SessionJoinResult(
            admitted: true,
            organizationId,
            workspaceId,
            sessionId,
            participantId,
            SessionJoinDenialReason.None);
    }

    /// <summary>
    /// Builds a fail-closed denial carrying no admitted identifiers. Internal so
    /// only <see cref="SessionParticipantJoinService"/> can mint one. A denial
    /// always carries a specific reason: <see cref="SessionJoinDenialReason.None"/>
    /// is reserved for an admission and is rejected here.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The reason is
    /// <see cref="SessionJoinDenialReason.None"/>.</exception>
    internal static SessionJoinResult Denied(SessionJoinDenialReason reason)
    {
        if (reason == SessionJoinDenialReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "A denial requires a specific reason.");
        }

        return new SessionJoinResult(
            admitted: false,
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            Guid.Empty,
            reason);
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the
    /// admission flag, the four surrogate ids and the denial reason. All are
    /// identifiers/authorization metadata, never free-form content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"SessionJoinResult admitted={Admitted} org={OrganizationId} ws={WorkspaceId} "
            + $"session={SessionId} participant={ParticipantId} reason={Reason}";
}
