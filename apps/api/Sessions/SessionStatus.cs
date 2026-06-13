namespace LiveCore.Api.Sessions;

/// <summary>
/// Lifecycle status of a <see cref="Session"/> (CORE-SES-002). A session is the
/// Core-owned "live or prepared run of a workspace" (docs/03_DOMAIN_LANGUAGE.md;
/// docs/05_MODULE_CONTRACTS.md: the Sessions module owns "session lifecycle" and
/// "session status"). The live-timeline states are the lifecycle states behind the
/// three persisted session lifecycle events in docs/09_EVENT_CATALOG.md:
/// <c>SessionCreated</c> -&gt; <c>SessionStarted</c> ("starts live timeline") -&gt;
/// <c>SessionEnded</c> ("ends live timeline"). Those EVENTS are emitted by the
/// later start/end command stories (CORE-SES-004); the aggregate models the state
/// machine and its persistence.
///
/// <see cref="Cancelled"/> is the off-ramp added by the session cancel command
/// (CORE-LIFE-010, the "Resource Lifecycle and Deletion" epic): a host can cancel a
/// not-yet-started (still <see cref="Prepared"/>) session so it never runs. Like the
/// workspace archive (CORE-LIFE-009), the story's recorded decision is a SOFT,
/// terminal status transition rather than a hard delete, because a session is the
/// foreign-key target of the append-only <c>session_events</c> stream and the
/// <c>audit_logs</c> trail, which must NEVER be cascade-erased
/// (docs/10_DATABASE_SCHEMA.md: "audit logs are append-only"; the story note "NEVER
/// delete append-only session_events or audit_logs - prefer a Cancelled status").
///
/// The status is persisted by its stable name (not its numeric value), so the
/// integers below are only in-memory storage discriminators and carry no ordering
/// meaning; they must not be compared with &gt;/&lt;. The legal transitions are
/// expressed by the <see cref="Session"/> state machine
/// (<see cref="Session.Start"/>/<see cref="Session.End"/>/<see cref="Session.Cancel"/>),
/// not by integer order.
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
    /// is a terminal state; an ended session can be neither started nor ended
    /// again. Re-running a workspace is a new session, not a transition back out of
    /// this state.
    /// </summary>
    Ended = 3,

    /// <summary>
    /// The session was cancelled before it ever ran — the soft, terminal off-ramp of
    /// the session cancel command (CORE-LIFE-010). A session may be cancelled ONLY from
    /// <see cref="Prepared"/> (a not-yet-started session), so a cancelled session never
    /// opened a live timeline (<see cref="Session.StartedAt"/> and
    /// <see cref="Session.EndedAt"/> stay null). This is a terminal state, reached
    /// instead of a hard delete so the session row survives to anchor its append-only
    /// <c>session_events</c> and <c>audit_logs</c> history (the story note: "NEVER
    /// delete append-only session_events or audit_logs - prefer a Cancelled status").
    /// A cancelled session can be neither started, ended nor cancelled again; running
    /// the workspace later is a new session, not a transition back out of this state.
    /// </summary>
    Cancelled = 4,
}
