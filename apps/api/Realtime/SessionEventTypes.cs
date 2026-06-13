namespace LiveCore.Api.Realtime;

/// <summary>
/// The Core-level catalog of session event type names (CORE-RT-003), the product-neutral
/// <c>eventType</c> values of docs/09_EVENT_CATALOG.md. Names are generic Core events, never vertical
/// terms. Extensible: later stories add members as their commands begin emitting events (the start/end
/// lifecycle events were wired by CORE-EVT-001; the remaining catalog events follow).
/// </summary>
public static class SessionEventTypes
{
    /// <summary>
    /// A session moved from <c>Prepared</c> to <c>Live</c> — the Sessions module's start command
    /// (docs/09_EVENT_CATALOG.md: "SessionStarted | Host/CoHost | session audience | yes | starts live
    /// timeline"). Emitted by the start endpoint (CORE-EVT-001) when a session actually starts. Unlike
    /// <see cref="ContentRevealed"/> this is a SUBJECTLESS audience event (no visibility subject, no
    /// selected participant), so the recipient resolver delivers it unconditionally to the session hosts,
    /// the observers and every active participant — the whole session audience.
    /// </summary>
    public const string SessionStarted = "SessionStarted";

    /// <summary>
    /// A session moved from <c>Live</c> to <c>Ended</c> — the Sessions module's end command
    /// (docs/09_EVENT_CATALOG.md: "SessionEnded | Host/CoHost | session audience | yes | ends live
    /// timeline"). Emitted by the end endpoint (CORE-EVT-001) when a session actually ends. Like
    /// <see cref="SessionStarted"/> it is a subjectless audience event delivered to the whole session
    /// audience.
    /// </summary>
    public const string SessionEnded = "SessionEnded";

    /// <summary>
    /// A participant joined a session — the Sessions module's join flow
    /// (docs/09_EVENT_CATALOG.md: "ParticipantJoined | Participant/System | Host/CoHost, maybe audience |
    /// yes | join visibility configurable"; csv/event_catalog.csv: "Host/CoHost and configured audience").
    /// Emitted by the join flow (CORE-EVT-002, <see cref="LiveCore.Api.Sessions.SessionParticipantJoinService"/>)
    /// when a participant is actually admitted to a session. Like <see cref="SessionStarted"/> it is a
    /// SUBJECTLESS audience event (no visibility subject, no selected participant), so the recipient resolver
    /// delivers it to the session hosts (always — host-visible), the observers and every active participant
    /// (the configurable audience). The payload carries the participant IDENTIFIER only — never a display
    /// name or any other participant PII (threat T7); the actor is the joining participant's user (or the
    /// system, for an anonymous participant).
    /// </summary>
    public const string ParticipantJoined = "ParticipantJoined";

    /// <summary>
    /// A participant left a session — the Sessions module's leave flow over <c>Participant.Remove</c>
    /// (docs/09_EVENT_CATALOG.md: "ParticipantLeft | System | Host/CoHost | yes | participant feed
    /// optional"). Emitted by the leave flow (CORE-EVT-002,
    /// <see cref="LiveCore.Api.Sessions.SessionParticipantLeaveService"/>) when a participant is actually
    /// removed (soft-deleted) from a session's audience. Like <see cref="ParticipantJoined"/> it is a
    /// subjectless audience event delivered to the hosts (always — host-visible) and the remaining audience;
    /// because the participant is removed BEFORE the event is published, the just-departed participant is no
    /// longer in the active-participant fan-out, so a leaver never receives their own removal (the optional
    /// participant feed). The payload carries the participant IDENTIFIER only — never a display name or any
    /// other PII (threat T7) — and the actor is the system (a System-emitted event).
    /// </summary>
    public const string ParticipantLeft = "ParticipantLeft";

    /// <summary>
    /// A host revealed a resource to the audience or to a selected participant — the central
    /// participant-facing event (docs/09_EVENT_CATALOG.md: "ContentRevealed | Host/CoHost | selected
    /// recipients | yes | central event"). Emitted by the reveal command (CORE-VIS-004/005) when a
    /// reveal actually changes visibility.
    /// </summary>
    public const string ContentRevealed = "ContentRevealed";

    /// <summary>
    /// A host hid (un-revealed) a resource — the inverse of <see cref="ContentRevealed"/>, instructing
    /// recipients who could see the resource to stop showing it (docs/09_EVENT_CATALOG.md:
    /// "ContentHidden"). Emitted by the hide command (CORE-REV-001) when a hide actually changes
    /// visibility. Unlike a reveal, a hide event carries NO visibility subject: the resource is now hidden,
    /// so a subject-gated projection would (correctly, for a reveal) exclude the very audience that must be
    /// told to remove it; instead the event is routed by its coarse target — a selected-participant hide
    /// reaches only that participant (plus hosts), an audience-wide hide reaches the observers and every
    /// active participant — and carries resource IDENTIFIERS only, never content (threats T2/T3/T7).
    /// </summary>
    public const string ContentHidden = "ContentHidden";
}
