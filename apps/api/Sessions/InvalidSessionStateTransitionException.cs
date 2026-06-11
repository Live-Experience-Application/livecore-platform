namespace LiveCore.Api.Sessions;

/// <summary>
/// Thrown when a <see cref="Session"/> lifecycle transition
/// (<see cref="Session.Start"/> or <see cref="Session.End"/>) is attempted from a
/// state in which it is not legal (CORE-SES-002). The session state machine is
/// <see cref="SessionStatus.Prepared"/> -&gt; <see cref="SessionStatus.Live"/> -&gt;
/// <see cref="SessionStatus.Ended"/>, and each transition is valid from exactly
/// one predecessor state; every other attempt is a genuine state error, not a
/// no-op (docs/09_EVENT_CATALOG.md: <c>SessionStarted</c> "starts live timeline",
/// <c>SessionEnded</c> "ends live timeline" — both presuppose the correct prior
/// state).
///
/// The throw is deliberate and chosen over a silent no-op (unlike
/// <c>Participant.Remove</c>): a lifecycle that drives a live timeline through
/// <see cref="Session.StartedAt"/>/<see cref="Session.EndedAt"/> must reject an
/// out-of-order start or end rather than absorb it, so a double-start or
/// end-before-start surfaces as an error instead of corrupting the timeline.
/// Command-level retry idempotency (an at-most-once start/end command) is the
/// later CORE-SES-004 concern (the <c>idempotency_keys</c> table), not an
/// aggregate no-op. So the application layer can branch WITHOUT catching, the
/// aggregate also exposes the <see cref="Session.CanStart"/> and
/// <see cref="Session.CanEnd"/> predicates; this exception is the fail-closed
/// guard for callers that do not pre-check.
///
/// The message carries only the aggregate's identifiers and the from/to status
/// names (never the title), so it is safe for structured logs (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class InvalidSessionStateTransitionException : InvalidOperationException
{
    /// <summary>
    /// Creates a transition error describing the session, the status it is in and
    /// the status the rejected transition targeted.
    /// </summary>
    public InvalidSessionStateTransitionException(Guid sessionId, SessionStatus from, SessionStatus to)
        : base($"Session {sessionId} cannot transition from {from} to {to}.")
    {
        SessionId = sessionId;
        From = from;
        To = to;
    }

    /// <summary>The id of the session whose transition was rejected.</summary>
    public Guid SessionId { get; }

    /// <summary>The status the session was in when the transition was attempted.</summary>
    public SessionStatus From { get; }

    /// <summary>The status the rejected transition targeted.</summary>
    public SessionStatus To { get; }
}
