namespace LiveCore.Api.Realtime;

/// <summary>
/// An append-only session event (CORE-RT-003, "event append and delivery"). The Realtime module owns the
/// session-scoped <c>session_events</c> table — the "Append-only event stream" that "drive[s] live state
/// reconstruction" (csv/database_tables.csv: module Realtime, scope <c>session</c>;
/// docs/09_EVENT_CATALOG.md; docs/10_DATABASE_SCHEMA.md: "session events are append-only"). A
/// <see cref="SessionEvent"/> is the durable record of one thing that happened in a session; persisting
/// it is the "persist event" step of the documented delivery flow ("command -> authorize -> persist
/// event -> compute recipients -> project payload -> send to recipient groups", docs/11_REALTIME_SYNC.md).
///
/// FIELDS follow docs/09_EVENT_CATALOG.md's event principles: <see cref="Id"/> (eventId),
/// <see cref="OrganizationId"/>, <see cref="WorkspaceId"/>, <see cref="SessionId"/>,
/// <see cref="EventType"/>, <see cref="CreatedBy"/>, <see cref="CreatedAt"/>, <see cref="Payload"/> and
/// <see cref="SchemaVersion"/>. <see cref="TargetParticipantId"/> is the coarse recipient routing (a
/// single selected participant, or audience-wide when null) that decides which server-managed GROUPS the
/// event is delivered to (the same routing the CORE-VIS-005 selected-participant reveal already uses).
///
/// VISIBILITY SUBJECT (CORE-RT-004, the documented <c>visibilityProjection</c> input). The event also
/// records the Core resource whose audience visibility GATES who may receive it —
/// <see cref="VisibilitySubjectType"/> (the generic resource kind name, e.g. <c>Entity</c>) and
/// <see cref="VisibilitySubjectId"/>. This is what lets the Realtime delivery compute recipients
/// per-RECIPIENT through the central Visibility engine: an audience-wide event is fanned out to each
/// active participant only when that participant may see the subject, and a private event reaches only
/// its selected participant (threat T3 "per-recipient event projection", docs/07_SECURITY_THREAT_MODEL.md;
/// docs/11_REALTIME_SYNC.md "Participant events must contain only data visible to that participant"). The
/// subject is recorded as a (type-NAME string, id) pair — the resource kind is a string so the Realtime
/// module stays DECOUPLED from the Visibility enum (mirroring the Audit log's recorded resource type),
/// and the id is a polymorphic surrogate (a scene/content-block/entity), intentionally NOT a foreign key
/// (mirroring <c>visibility_rules.resource_id</c>). Both are nullable and travel TOGETHER: an event
/// either carries a subject (both set) or none (both null, an unconditional audience event such as a
/// later <c>SessionStarted</c>). The subject is server-composed identifiers only — never resolved
/// content (threat T7) — and is internal addressing, so it is excluded from the recipient-safe envelope.
///
/// APPEND-ONLY (docs/10_DATABASE_SCHEMA.md). Every property is get-only; there is no mutator, no setter
/// and no delete on the repository. An event, once appended, is an immutable historical fact — that is
/// what lets reconnect replay (CORE-RT-005) reconstruct state from the stream.
///
/// TENANT + SESSION boundary: the event is session-scoped, so it carries <see cref="OrganizationId"/>
/// (the tenant, checked first), <see cref="WorkspaceId"/> and <see cref="SessionId"/>, mirroring every
/// other workspace/session-scoped aggregate (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). The
/// documented critical index is <c>session_events(session_id, created_at, event_id)</c>
/// (docs/10_DATABASE_SCHEMA.md). The surrogate id alone never authorizes access: every read is scoped by
/// tenant and session.
///
/// NO SENSITIVE CONTENT BEYOND THE PAYLOAD (threat T7). The identifier/enum fields are log-safe; the
/// <see cref="Payload"/> is a bounded, server-composed JSON document of resource IDENTIFIERS (never the
/// resolved content body), so <see cref="ToString"/> excludes it.
/// </summary>
public sealed class SessionEvent
{
    /// <summary>Maximum stored length of the generic event-type name.</summary>
    public const int MaxEventTypeLength = 64;

    /// <summary>Maximum stored length of the visibility-subject resource-type name.</summary>
    public const int MaxVisibilitySubjectTypeLength = 64;

    /// <summary>Maximum stored length of the server-composed JSON payload.</summary>
    public const int MaxPayloadLength = 8192;

    private SessionEvent(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        string eventType,
        Guid? createdBy,
        Guid? targetParticipantId,
        string payload,
        int schemaVersion,
        string? visibilitySubjectType,
        Guid? visibilitySubjectId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session event id must not be empty.", nameof(id));
        }

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

        if (!IsValidEventType(eventType))
        {
            throw new ArgumentException("Event type violates the event-type invariants.", nameof(eventType));
        }

        // A null actor means a system-generated event; a SET actor must be a real user profile id.
        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException(
                "Created-by user profile id must not be empty; pass null for a system event.",
                nameof(createdBy));
        }

        // A null target means audience-wide; a SET target must be a real participant id.
        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null for an audience-wide event.",
                nameof(targetParticipantId));
        }

        if (!IsValidPayload(payload))
        {
            throw new ArgumentException("Payload violates the payload invariants.", nameof(payload));
        }

        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be at least 1.");
        }

        // The visibility subject (type + id) is a pair that travels together: an event either carries a
        // subject (both set) or none (both null). A half-set subject could not gate recipients correctly,
        // so it is rejected rather than silently degraded (threat T3). A set subject must name a real
        // resource: a non-blank, valid type and a non-empty id.
        var normalizedSubjectType = string.IsNullOrWhiteSpace(visibilitySubjectType)
            ? null
            : visibilitySubjectType.Trim();
        var hasSubjectType = normalizedSubjectType is not null;
        var hasSubjectId = visibilitySubjectId is not null;
        if (hasSubjectType != hasSubjectId)
        {
            throw new ArgumentException(
                "The visibility subject type and id must be provided together (both set) or both omitted.",
                nameof(visibilitySubjectType));
        }

        if (hasSubjectId)
        {
            if (visibilitySubjectId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Visibility subject id must not be empty; pass null for an event with no subject.",
                    nameof(visibilitySubjectId));
            }

            if (!IsValidVisibilitySubjectType(normalizedSubjectType))
            {
                throw new ArgumentException(
                    "Visibility subject type violates the subject-type invariants.",
                    nameof(visibilitySubjectType));
            }
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SessionId = sessionId;
        EventType = eventType;
        CreatedBy = createdBy;
        TargetParticipantId = targetParticipantId;
        Payload = payload;
        SchemaVersion = schemaVersion;
        VisibilitySubjectType = normalizedSubjectType;
        VisibilitySubjectId = visibilitySubjectId;
        // Normalized to UTC so the persisted timestamptz value is offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private SessionEvent()
    {
        EventType = null!;
        Payload = null!;
    }

    /// <summary>Surrogate key of the event row (eventId; UUID version 7, time-ordered per docs/10).</summary>
    public Guid Id { get; }

    /// <summary>
    /// The per-session, gap-free, strictly monotonic event sequence number (CORE-RTC-001). It is the
    /// AUTHORITATIVE ordering and replay key for the session stream — NOT the UUIDv7 <see cref="Id"/>, which
    /// is only monotonic at millisecond resolution and so reorders events appended within the same
    /// millisecond. The number is allocated per session at APPEND time (by the repository, inside the
    /// command's unit-of-work transaction; <see cref="SessionEventRepository"/>), starting at 1 and
    /// incrementing by exactly one per appended event, so events appended in the same millisecond preserve
    /// their append order and a client detects a missed event by a gap in the sequence. It is 0 (unassigned)
    /// on a freshly <see cref="Create"/>d event until the append path assigns it.
    /// </summary>
    public long Sequence { get; private set; }

    /// <summary>Tenant boundary of the event (the <c>organization_id</c> foreign key to <c>organizations</c>).</summary>
    public Guid OrganizationId { get; }

    /// <summary>Workspace boundary of the event (the <c>workspace_id</c> foreign key to <c>workspaces</c>).</summary>
    public Guid WorkspaceId { get; }

    /// <summary>The session whose append-only stream this event belongs to (the <c>session_id</c> foreign key).</summary>
    public Guid SessionId { get; }

    /// <summary>
    /// The generic event type (docs/09_EVENT_CATALOG.md), a product-neutral Core event name such as
    /// <c>ContentRevealed</c>. Stored as a string so new event types need no schema change.
    /// </summary>
    public string EventType { get; }

    /// <summary>
    /// The user profile that produced the event (docs/09_EVENT_CATALOG.md <c>createdBy</c>), or
    /// <see langword="null"/> for a system-generated event.
    /// </summary>
    public Guid? CreatedBy { get; }

    /// <summary>
    /// The single participant this event is routed to (a selected-participant event, CORE-VIS-005), or
    /// <see langword="null"/> for an audience-wide event. This is the coarse recipient routing the
    /// delivery uses to pick the server-managed group(s); the per-recipient payload projection is
    /// CORE-RT-004.
    /// </summary>
    public Guid? TargetParticipantId { get; }

    /// <summary>
    /// The server-composed JSON payload of the event — resource IDENTIFIERS only, never resolved content
    /// (threat T7). Bounded by <see cref="MaxPayloadLength"/>.
    /// </summary>
    public string Payload { get; }

    /// <summary>
    /// The generic kind NAME of the Core resource whose audience visibility GATES who may receive this
    /// event (CORE-RT-004, the <c>visibilityProjection</c> input of docs/09_EVENT_CATALOG.md), or
    /// <see langword="null"/> when the event has no visibility subject (an unconditional audience event).
    /// Stored as a string to keep the Realtime module decoupled from the Visibility resource-type enum
    /// (mirrors the Audit log's recorded resource type). Always paired with <see cref="VisibilitySubjectId"/>.
    /// </summary>
    public string? VisibilitySubjectType { get; }

    /// <summary>
    /// The surrogate id of the resource whose audience visibility gates who may receive this event
    /// (CORE-RT-004), or <see langword="null"/> when the event has no visibility subject. A polymorphic
    /// reference across <c>scenes</c>/<c>content_blocks</c>/<c>entities</c>, intentionally NOT a foreign
    /// key (mirrors <c>visibility_rules.resource_id</c>). Always paired with
    /// <see cref="VisibilitySubjectType"/>.
    /// </summary>
    public Guid? VisibilitySubjectId { get; }

    /// <summary>
    /// The payload schema version (docs/09_EVENT_CATALOG.md: "Breaking event payload changes require a
    /// new schema version"). At least 1.
    /// </summary>
    public int SchemaVersion { get; }

    /// <summary>When the event happened (UTC). Part of the critical index with the session.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Whether this event is routed to a single selected participant rather than the audience.</summary>
    public bool IsForSelectedParticipant => TargetParticipantId is not null;

    /// <summary>
    /// Whether this event carries a visibility subject (CORE-RT-004): a Core resource (type + id) whose
    /// audience visibility gates who may receive the event. When <see langword="false"/> the event has no
    /// subject and is delivered to the audience unconditionally.
    /// </summary>
    public bool HasVisibilitySubject => VisibilitySubjectType is not null && VisibilitySubjectId is not null;

    /// <summary>
    /// Appends a new session event to the given session's stream (owned by the given workspace and
    /// tenant). The caller (a Realtime publisher invoked by a command) supplies the already-resolved ids,
    /// the event type, the optional actor, the optional recipient participant, the server-composed
    /// payload and the schema version. The optional visibility subject (CORE-RT-004) — the resource kind
    /// NAME and id whose audience visibility gates who may receive the event — defaults to none (an
    /// unconditional audience event) and must be passed as a pair (both set or both omitted).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A required id is empty, an optional id is explicitly empty, the event type / payload violates an
    /// invariant, or only one of the visibility-subject type / id is supplied.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The schema version is below 1.</exception>
    public static SessionEvent Create(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        string eventType,
        Guid? createdBy,
        Guid? targetParticipantId,
        string payload,
        int schemaVersion,
        DateTimeOffset createdAt,
        string? visibilitySubjectType = null,
        Guid? visibilitySubjectId = null)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            sessionId,
            eventType?.Trim() ?? string.Empty,
            createdBy,
            targetParticipantId,
            payload ?? string.Empty,
            schemaVersion,
            visibilitySubjectType,
            visibilitySubjectId,
            createdAt);

    /// <summary>
    /// Assigns the per-session monotonic <see cref="Sequence"/> at APPEND time (CORE-RTC-001). The append
    /// path (<see cref="SessionEventRepository.AppendAsync"/>) allocates the next gap-free number for the
    /// event's session and stamps it here just before the row is persisted, so ordering and replay use the
    /// sequence rather than the millisecond-resolution UUIDv7 id. The sequence is assigned exactly once: a
    /// re-assignment or a non-positive value is rejected, since an event is an immutable historical fact and
    /// its sequence must be a real per-session position (at least 1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The sequence is below 1.</exception>
    /// <exception cref="InvalidOperationException">A sequence was already assigned.</exception>
    internal void AssignSequence(long sequence)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "A session event sequence must be at least 1.");
        }

        if (Sequence != 0)
        {
            throw new InvalidOperationException("The session event sequence has already been assigned.");
        }

        Sequence = sequence;
    }

    /// <summary>
    /// Whether the given value is a valid event type: non-blank, within the length bound and free of
    /// control characters.
    /// </summary>
    public static bool IsValidEventType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxEventTypeLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid payload: non-null and within the length bound. The content is
    /// server-composed (resource ids), so only structural bounds are enforced here.
    /// </summary>
    public static bool IsValidPayload(string? value)
        => value is not null && value.Length <= MaxPayloadLength;

    /// <summary>
    /// Whether the given value is a valid visibility-subject resource-type name: non-blank, within the
    /// length bound and free of control characters (same shape as the event type). The value is a
    /// server-composed resource kind name, so only structural bounds are enforced here.
    /// </summary>
    public static bool IsValidVisibilitySubjectType(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxVisibilitySubjectTypeLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Identifier-only representation safe for structured logs: event id, type, tenant, workspace,
    /// session, actor, target, visibility subject and schema version. The payload is excluded so it
    /// cannot leak through logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md); the subject is
    /// identifiers only.
    /// </summary>
    public override string ToString()
        => $"SessionEvent {Id} seq={Sequence} type={EventType} org={OrganizationId} ws={WorkspaceId} session={SessionId} "
            + $"by={(CreatedBy is { } actor ? actor.ToString() : "system")} "
            + $"target={(TargetParticipantId is { } target ? target.ToString() : "audience")} "
            + $"subject={(HasVisibilitySubject ? $"{VisibilitySubjectType}:{VisibilitySubjectId}" : "none")} v={SchemaVersion}";
}
