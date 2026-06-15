namespace LiveCore.Api.Realtime;

/// <summary>
/// The Core-level catalog of session event type names (CORE-RT-003), the product-neutral
/// <c>eventType</c> values of docs/09_EVENT_CATALOG.md. Names are generic Core events, never vertical
/// terms. Extensible: later stories add members as their commands begin emitting events (the start/end
/// lifecycle events were wired by CORE-EVT-001; <see cref="SessionCreated"/> and <see cref="RecapGenerated"/>
/// by CORE-EVT-004).
///
/// <para>
/// CATALOG-AS-CONTRACT (CORE-EVT-004). This set is the SINGLE source of the emitted session-event
/// vocabulary, and the spec-consistency check (scripts/spec-consistency.ps1, check 11) binds it to
/// csv/event_catalog.csv: every constant here must be a NON-deferred catalog row, and every non-deferred
/// catalog row must have a constant here. So the catalog can no longer list a session event that no command
/// emits (the session-event analogue of CORE-SPEC-002, which made the entitlement/store catalog real). A
/// catalog event that has no Core command yet — for example the workspace-prepared <c>SceneCreated</c> /
/// <c>ContentBlockCreated</c>, which carry no session and so cannot be session-scoped events without an
/// active-scene pointer (a future Sessions story) — stays in the catalog marked deferred and is deliberately
/// absent here.
/// </para>
/// </summary>
public static class SessionEventTypes
{
    /// <summary>
    /// A session was created in a workspace — the Sessions module's create command
    /// (docs/09_EVENT_CATALOG.md: "SessionCreated | Host/Admin | Host/Admin/CoHost | yes | not always
    /// participant-visible"). Emitted by the create endpoint (CORE-EVT-004) when a session is actually
    /// created (the <c>Prepared</c> state the session starts in), appended to its OWN session's stream inside
    /// the create command's unit of work (CORE-CONC-002). Unlike the subjectless lifecycle events
    /// (<see cref="SessionStarted"/>), which reach the WHOLE session audience, this is a HOST-ONLY
    /// preparation event (<see cref="IsHostOnly"/>): the catalog makes it visible to the hosts only, so the
    /// recipient resolver delivers it to the session hosts and to no observer or participant — never the
    /// audience — both live and on reconnect replay. The payload carries the session IDENTIFIER and its
    /// lifecycle status only, never any content (threat T7); the actor is the host who created the session.
    /// </summary>
    public const string SessionCreated = "SessionCreated";

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
    /// A host activated a scene — the "scene switch" (docs/09_EVENT_CATALOG.md: "SceneActivated |
    /// Host/CoHost | authorized session audience | yes | scene switch"). Emitted by the reveal command
    /// (CORE-EVT-003) when a reveal actually makes a <see cref="LiveCore.Api.Visibility.VisibilityResourceType.Scene"/>
    /// visible — there is no separate active-scene command, so revealing a scene to the audience IS the
    /// scene switch. Unlike the subjectless lifecycle events (<see cref="SessionStarted"/>) this event
    /// CONCERNS A GOVERNED RESOURCE, so it carries the activated scene as its VISIBILITY SUBJECT (the
    /// (Scene, id) pair, CORE-RT-004): the recipient resolver gates it through the central Visibility
    /// engine, delivering it to the hosts and to exactly the audience that may see the scene, so a
    /// participant for whom the scene is hidden never receives the activation (threats T2/T3). The payload
    /// carries the scene IDENTIFIER only, never resolved content (threat T7).
    /// </summary>
    public const string SceneActivated = "SceneActivated";

    /// <summary>
    /// A host changed a resource's visibility rule — the security-relevant rule-change event
    /// (docs/09_EVENT_CATALOG.md: "VisibilityRuleChanged | Host/CoHost | Host/CoHost/Auditor | yes |
    /// security relevant"). Emitted by the reveal AND hide commands (CORE-EVT-003) whenever a command
    /// actually changes a rule's visibility — the same change signal the append-only audit record uses
    /// (CORE-VIS-006), but this is the durable REALTIME session event, DISTINCT from that audit record.
    /// It CONCERNS A GOVERNED RESOURCE, so it carries the changed resource as its VISIBILITY SUBJECT (the
    /// (resource kind, id) pair, CORE-RT-004) and the recipient resolver gates it through the central
    /// Visibility engine: the hosts always receive it (host-content roles see everything), and the
    /// audience receives it only when the rule's NEW state lets them see the resource. So a HIDE (the
    /// resource is now hidden) reaches ONLY the hosts — the security-relevant, host-facing case — and a
    /// REVEAL additionally reaches the audience that may now see it; a participant for whom the resource is
    /// hidden never receives the event (no leakage of hidden resources, threats T2/T3). The payload carries
    /// the resource IDENTIFIERS and the new visibility STATE name only, never resolved content (threat T7).
    /// </summary>
    public const string VisibilityRuleChanged = "VisibilityRuleChanged";

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

    /// <summary>
    /// A recap was generated for a session — the Recaps module's recap-generation job
    /// (docs/09_EVENT_CATALOG.md: "RecapGenerated | Host/System | Host/CoHost/Admin | yes | participant
    /// recap requires separate reveal"). Emitted by the background recap worker (CORE-EVT-004,
    /// <see cref="LiveCore.Api.Recaps.RecapGenerationService"/>) when a recap is actually produced for an
    /// ENDED session, appended to that session's stream as a SYSTEM event (no actor). Like
    /// <see cref="SessionCreated"/> it is a HOST-ONLY event (<see cref="IsHostOnly"/>): a generated recap is
    /// "Participant-visible only after separate reveal", so the recipient resolver delivers it to the session
    /// hosts only — never an observer or participant — both live and on reconnect replay, so the recap's
    /// existence never leaks to the audience before a host reveals it (threats T2/T7). The payload carries
    /// the recap and session IDENTIFIERS only, never the recap body.
    /// </summary>
    public const string RecapGenerated = "RecapGenerated";

    /// <summary>
    /// The HOST-ONLY session events (CORE-EVT-004): preparation/output events the catalog marks visible to
    /// the hosts only — a generated recap is "Participant-visible only after separate reveal", and a created
    /// session is "not always participant-visible" (docs/09_EVENT_CATALOG.md; csv/event_catalog.csv). They
    /// are durable and host-facing, so the recipient resolver delivers them to the session hosts and to NO
    /// observer or participant, both live and on reconnect replay — even after a resource the event concerns
    /// is later revealed to the audience, the prep/output event itself never reaches the audience. This is a
    /// subject-INDEPENDENT routing class (unlike <see cref="SceneActivated"/>, whose audience changes as the
    /// scene's visibility changes), so it is recorded as Core routing policy on the catalog rather than
    /// derived from the event's visibility subject.
    /// </summary>
    private static readonly HashSet<string> _hostOnlyEventTypes = new(StringComparer.Ordinal)
    {
        SessionCreated,
        RecapGenerated,
    };

    /// <summary>
    /// Whether the given event type is a HOST-ONLY event (see <see cref="_hostOnlyEventTypes"/>): a
    /// preparation/output event the catalog routes to the session hosts only, never the audience. The
    /// recipient resolver (<see cref="SessionEventRecipientResolver"/>) consults this to deliver such an
    /// event to the hosts group alone, both for live delivery and on reconnect replay, so the routing can
    /// never widen to an observer or participant.
    /// </summary>
    public static bool IsHostOnly(string eventType) => _hostOnlyEventTypes.Contains(eventType);
}
