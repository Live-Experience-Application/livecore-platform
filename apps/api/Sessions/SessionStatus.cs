namespace LiveCore.Api.Sessions;

/// <summary>
/// Lifecycle status of a <see cref="Session"/> (CORE-SES-002). A session is the
/// Core-owned "live or prepared run of a workspace" (docs/03_DOMAIN_LANGUAGE.md;
/// docs/05_MODULE_CONTRACTS.md: the Sessions module owns "session lifecycle" and
/// "session status"). The three states are the lifecycle states behind the three
/// persisted session lifecycle events in docs/09_EVENT_CATALOG.md:
/// <c>SessionCreated</c> -&gt; <c>SessionStarted</c> ("starts live timeline") -&gt;
/// <c>SessionEnded</c> ("ends live timeline"). Those EVENTS are emitted by the
/// later start/end command stories (CORE-SES-004); this story models only the
/// aggregate, its state machine and its persistence.
///
/// The status is persisted by its stable name (not its numeric value), so the
/// integers below are only in-memory storage discriminators and carry no ordering
/// meaning; they must not be compared with &gt;/&lt;. The legal transitions are
/// expressed by the <see cref="Session"/> state machine
/// (<see cref="Session.Start"/>/<see cref="Session.End"/>), not by integer order.
/// </summary>
public enum SessionStatus
{
    /// <summary>
    /// The session has been created but is not yet live: it has a configured shape
    /// but no live timeline (the state behind <c>SessionCreated</c> in
    /// docs/09_EVENT_CATALOG.md, "not always participant-visible"). This is the
    /// only state from which the session may be started.
    /// </summary>
    Prepared = 1,

    /// <summary>
    /// The session is live: its live timeline has started (the state behind
    /// <c>SessionStarted</c> in docs/09_EVENT_CATALOG.md, "starts live timeline").
    /// This is the only state from which the session may be ended.
    /// </summary>
    Live = 2,

    /// <summary>
    /// The session has ended: its live timeline is closed (the state behind
    /// <c>SessionEnded</c> in docs/09_EVENT_CATALOG.md, "ends live timeline"). This
    /// is the terminal state; an ended session can be neither started nor ended
    /// again. Re-running a workspace is a new session, not a transition back out of
    /// this state.
    /// </summary>
    Ended = 3,
}
