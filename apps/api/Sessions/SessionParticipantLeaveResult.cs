namespace LiveCore.Api.Sessions;

/// <summary>
/// Outcome of removing a participant from a session through the
/// <see cref="SessionParticipantLeaveService"/> (CORE-EVT-002): the symmetric leave to the
/// <see cref="SessionParticipantJoinService"/>'s join. It is either a recorded departure (the participant
/// was soft-removed and the durable <c>ParticipantLeft</c> event delivered), an idempotent no-op (the
/// participant had already left), or a fail-closed hidden not-found, never a partial result (mirrors the
/// fail-closed <see cref="SessionJoinResult"/>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// The validated identifiers are exposed only when the participant was actually located WITHIN the
/// requested (organization, workspace) — i.e. on <see cref="ParticipantLeaveOutcome.Left"/> or
/// <see cref="ParticipantLeaveOutcome.AlreadyLeft"/>. A not-found outcome carries no identifiers, hiding
/// existence so nothing outside the caller's tenant/workspace is ever leaked (threat T1/T5). The whole type
/// is identifiers and a reason only, so it is safe for structured logs (threat T7).
/// </summary>
public sealed class SessionParticipantLeaveResult
{
    private SessionParticipantLeaveResult(
        ParticipantLeaveOutcome outcome,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
    {
        Outcome = outcome;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SessionId = sessionId;
        ParticipantId = participantId;
    }

    /// <summary>What happened to the leave request.</summary>
    public ParticipantLeaveOutcome Outcome { get; }

    /// <summary>Tenant boundary of the located participant. <see cref="Guid.Empty"/> on a not-found.</summary>
    public Guid OrganizationId { get; }

    /// <summary>Workspace boundary of the located participant. <see cref="Guid.Empty"/> on a not-found.</summary>
    public Guid WorkspaceId { get; }

    /// <summary>Surrogate id of the session the participant left. <see cref="Guid.Empty"/> on a not-found.</summary>
    public Guid SessionId { get; }

    /// <summary>Surrogate id of the participant that left. <see cref="Guid.Empty"/> on a not-found.</summary>
    public Guid ParticipantId { get; }

    /// <summary>Whether the participant was actually removed and the <c>ParticipantLeft</c> event delivered.</summary>
    public bool Left => Outcome == ParticipantLeaveOutcome.Left;

    /// <summary>
    /// Builds the result of an actual departure: the participant was active, has been soft-removed and the
    /// durable <c>ParticipantLeft</c> event delivered. Internal so only
    /// <see cref="SessionParticipantLeaveService"/> can mint one, and only after the session and participant
    /// were both loaded inside the (organization, workspace) boundary, so a departure can never carry foreign
    /// data (threat T5). All four ids must be non-empty.
    /// </summary>
    /// <exception cref="ArgumentException">Any identifier is empty.</exception>
    internal static SessionParticipantLeaveResult Departed(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
        => ForLocated(ParticipantLeaveOutcome.Left, organizationId, workspaceId, sessionId, participantId);

    /// <summary>
    /// Builds the result of an idempotent no-op: the participant was located but had already left (its status
    /// was already removed), so nothing changed and no event was emitted. Internal; the same non-empty-id
    /// invariant as <see cref="Departed"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Any identifier is empty.</exception>
    internal static SessionParticipantLeaveResult AlreadyLeft(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid participantId)
        => ForLocated(ParticipantLeaveOutcome.AlreadyLeft, organizationId, workspaceId, sessionId, participantId);

    /// <summary>
    /// Builds a fail-closed not-found carrying no identifiers: the session
    /// (<see cref="ParticipantLeaveOutcome.SessionNotFound"/>) or the participant
    /// (<see cref="ParticipantLeaveOutcome.ParticipantNotFound"/>) does not exist within the requested
    /// (organization, workspace), so its existence is hidden and no event is emitted (threat T1/T5). Internal;
    /// a located outcome is rejected here.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is a located one rather than a not-found.</exception>
    internal static SessionParticipantLeaveResult NotFound(ParticipantLeaveOutcome outcome)
    {
        if (outcome is not (ParticipantLeaveOutcome.SessionNotFound or ParticipantLeaveOutcome.ParticipantNotFound))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A not-found requires a not-found outcome.");
        }

        return new SessionParticipantLeaveResult(outcome, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
    }

    private static SessionParticipantLeaveResult ForLocated(
        ParticipantLeaveOutcome outcome,
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

        return new SessionParticipantLeaveResult(outcome, organizationId, workspaceId, sessionId, participantId);
    }

    /// <summary>
    /// Identifier-only representation safe for structured logs: the outcome and the four surrogate ids, all
    /// identifiers/authorization metadata, never free-form content (threat T7).
    /// </summary>
    public override string ToString()
        => $"SessionParticipantLeaveResult outcome={Outcome} org={OrganizationId} ws={WorkspaceId} "
            + $"session={SessionId} participant={ParticipantId}";
}

/// <summary>
/// What happened when a participant was removed from a session by the
/// <see cref="SessionParticipantLeaveService"/> (CORE-EVT-002).
///
/// Values are identifier-only outcomes: they can be logged and mapped to a response without echoing another
/// tenant's data (threat T7). The two not-found outcomes deliberately hide existence — a session or
/// participant in another organization or workspace is reported as "not found", never as "forbidden", so a
/// caller cannot probe for resources outside its tenant (threat T1/T5).
/// </summary>
public enum ParticipantLeaveOutcome
{
    /// <summary>The participant was active and has been soft-removed; the <c>ParticipantLeft</c> event was delivered.</summary>
    Left = 0,

    /// <summary>
    /// The participant was located but had already left (its status was already
    /// <see cref="Participants.ParticipantStatus.Removed"/>), so nothing changed and no event was emitted —
    /// the leave is idempotent.
    /// </summary>
    AlreadyLeft = 1,

    /// <summary>
    /// No session with the requested id exists within the requested organization and workspace (also covers a
    /// session that lives only under another organization or workspace; hidden as not-found, threat T1/T5).
    /// </summary>
    SessionNotFound = 2,

    /// <summary>
    /// No participant with the requested id exists within the requested organization and workspace (also
    /// covers a participant that lives only under another organization or workspace; hidden as not-found,
    /// threat T1/T5).
    /// </summary>
    ParticipantNotFound = 3,
}
