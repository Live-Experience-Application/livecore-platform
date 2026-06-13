namespace LiveCore.Api.Sessions;

/// <summary>
/// Session aggregate of the Sessions module (CORE-SES-002).
///
/// A <see cref="Session"/> is the Core-owned, product-neutral "live or prepared
/// run of a workspace" (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md:
/// the Sessions module owns "session lifecycle", "session status" and the "active
/// scene pointer", and provides "start/pause/end session commands";
/// csv/database_tables.csv: table <c>sessions</c>, module Sessions, scope
/// <c>workspace</c>, "Live/prepared run"). It stores no vertical product
/// language — it is a generic session only, never a vertical-specific run term
/// (docs/04_PRODUCT_BOUNDARIES.md, csv/forbidden_core_terms.csv). Verticals map it
/// to their own vocabulary in their UI; Core persists only the generic resource
/// (docs/04: public contract <c>/sessions</c>).
///
/// Tenant boundary: the session is workspace-scoped and tenant-scoped, so it
/// carries both <see cref="OrganizationId"/> (the tenant; checked before the
/// workspace boundary) and <see cref="WorkspaceId"/>, mirroring
/// <c>Participant</c> and <c>WorkspaceMember</c> (docs/10_DATABASE_SCHEMA.md:
/// tenant-scoped tables include <c>organization_id</c>, workspace-scoped tables
/// include <c>workspace_id</c>; the documented critical index is
/// <c>sessions(workspace_id, id)</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). A session belongs to exactly one
/// organization and one workspace for its whole lifetime; it never crosses to
/// another tenant or workspace. The surrogate <see cref="Id"/> alone never
/// authorizes access: every lookup is scoped by <see cref="OrganizationId"/> and
/// <see cref="WorkspaceId"/> so one workspace's session can never be reached
/// through another workspace's or another tenant's id (threat T5; T1 broken
/// object-level authorization).
///
/// Identity invariant: the surrogate <see cref="Id"/> (UUIDv7) is the row's own
/// key and the foreign-key target that session-scoped tables (session events,
/// scenes) will reference. A session has NO natural key: it is identified only by
/// its surrogate id, so there is deliberately no slug/title uniqueness — two
/// sessions in one workspace may share a title and stay fully isolated.
///
/// Display invariant: <see cref="Title"/> is human-facing metadata only — the
/// minimal display identity a session needs to exist as an aggregate, the same
/// precedent as <c>Workspace.Name</c> and <c>Participant.DisplayName</c>. It is
/// never an identity, never an authorization input and never a tenant boundary:
/// two sessions may legitimately share a title while staying fully isolated
/// because their organization id, workspace id and id differ.
///
/// Lifecycle invariant (the heart of this story): a session moves through a guarded
/// state machine <see cref="SessionStatus.Prepared"/> -&gt;
/// <see cref="SessionStatus.Live"/> -&gt; <see cref="SessionStatus.Ended"/>, the
/// states behind the persisted <c>SessionCreated</c> -&gt; <c>SessionStarted</c>
/// -&gt; <c>SessionEnded</c> events (docs/09_EVENT_CATALOG.md). A session is
/// created <see cref="SessionStatus.Prepared"/> with no live timeline
/// (<see cref="StartedAt"/> and <see cref="EndedAt"/> are null); <see cref="Start"/>
/// is valid only from Prepared and opens the live timeline (sets
/// <see cref="StartedAt"/>); <see cref="End"/> is valid only from Live and closes
/// it (sets <see cref="EndedAt"/>, with <see cref="EndedAt"/> &gt;=
/// <see cref="StartedAt"/>). Every other start/end attempt is an
/// <see cref="InvalidSessionStateTransitionException"/>, NOT a no-op: a lifecycle
/// that drives a live timeline must reject re-start/re-end as a genuine state
/// error (command-level retry idempotency is the later CORE-SES-004 concern, the
/// <c>idempotency_keys</c> table). Callers that want to branch without catching
/// use <see cref="CanStart"/>/<see cref="CanEnd"/>. The events themselves and the
/// start/end COMMANDS are emitted by CORE-SES-004, not here.
///
/// Cancel off-ramp (CORE-LIFE-010): <see cref="Cancel"/> is valid only from
/// <see cref="SessionStatus.Prepared"/> and moves a not-yet-started session to the
/// soft, terminal <see cref="SessionStatus.Cancelled"/> — a status transition like
/// <see cref="Start"/>/<see cref="End"/> (and like <c>Workspace.Archive</c>), NOT a
/// hard delete, so the session row survives to anchor its append-only
/// <c>session_events</c> and <c>audit_logs</c> history. A cancelled session never
/// opened a live timeline, so <see cref="StartedAt"/>/<see cref="EndedAt"/> stay
/// null; once cancelled it can be neither started, ended nor cancelled again
/// (<see cref="CanStart"/>/<see cref="CanEnd"/>/<see cref="CanCancel"/> are all
/// false). Cancelling a session that already started, ended or was cancelled is an
/// <see cref="InvalidSessionStateTransitionException"/>, NOT a no-op; callers that
/// want to branch without catching use <see cref="CanCancel"/>.
///
/// There is deliberately no "Paused" state and no active-scene pointer here.
/// docs/09_EVENT_CATALOG.md documents no <c>SessionPaused</c> event and
/// csv/api_routes.csv has no pause route, so a persisted Paused state would be
/// speculative scope; docs/05 lists pause only as a future COMMAND (CORE-SES-004
/// territory). An active-scene pointer is omitted because the <c>scenes</c> table
/// does not exist yet (CORE-SCENE-001), so a column/foreign key now would be a
/// forward dependency. Both are noted as follow-ups.
/// </summary>
public sealed class Session
{
    /// <summary>
    /// Maximum accepted title length. The title is informational display metadata;
    /// longer values are rejected as hostile or broken input. Mirrors the
    /// <c>Workspace.Name</c> and <c>Participant.DisplayName</c> bound.
    /// </summary>
    public const int MaxTitleLength = 256;

    private Session(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string title,
        SessionStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!IsValidTitle(title))
        {
            throw new ArgumentException("Title violates the title invariants.", nameof(title));
        }

        if (!IsValidStatus(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Status is not a defined session status.");
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A session cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        Title = title;
        Status = status;
        // Timestamps are normalized to UTC so the persisted timestamptz values
        // are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
        StartedAt = startedAt?.ToUniversalTime();
        EndedAt = endedAt?.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Session()
    {
        Title = null!;
    }

    /// <summary>
    /// Surrogate key of the session row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). This is the value session-scoped tables (session
    /// events, scenes) will reference through their <c>session_id</c> column. On its
    /// own it never authorizes access: lookups are always scoped by
    /// <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the session: the id of the organization that owns the
    /// workspace this session belongs to (the <c>organization_id</c> foreign key to
    /// the <c>organizations</c> table). Immutable; a session never crosses to
    /// another organization. The organization boundary is checked before the
    /// workspace boundary, so this column lets isolation be enforced at the row
    /// level (threat T5; docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the session: the id of the workspace this session
    /// belongs to (the <c>workspace_id</c> foreign key to the <c>workspaces</c>
    /// table). Immutable; a session never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Human-readable display label of the session — the minimal display identity a
    /// session needs to exist as an aggregate (the same precedent as
    /// <c>Workspace.Name</c> and <c>Participant.DisplayName</c>). Informational
    /// metadata only: never an identity, never an authorization input, never a
    /// tenant boundary.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Lifecycle status of the session. A session is created
    /// <see cref="SessionStatus.Prepared"/> and moves through
    /// <see cref="SessionStatus.Live"/> to the terminal
    /// <see cref="SessionStatus.Ended"/> via the guarded <see cref="Start"/> and
    /// <see cref="End"/> transitions.
    /// </summary>
    public SessionStatus Status { get; private set; }

    /// <summary>When this session was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this session was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// When the session's live timeline started (UTC), or <see langword="null"/>
    /// while the session is still <see cref="SessionStatus.Prepared"/>. Set once by
    /// <see cref="Start"/> and never cleared, so it remains the live-timeline start
    /// boundary after the session has ended (the timestamp behind
    /// <c>SessionStarted</c>, docs/09_EVENT_CATALOG.md).
    /// </summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    /// When the session's live timeline ended (UTC), or <see langword="null"/>
    /// until the session reaches <see cref="SessionStatus.Ended"/>. Set once by
    /// <see cref="End"/>, always at or after <see cref="StartedAt"/> (the timestamp
    /// behind <c>SessionEnded</c>, docs/09_EVENT_CATALOG.md).
    /// </summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// Whether the session is currently live: it has started and has not yet ended
    /// (<see cref="SessionStatus.Live"/>).
    /// </summary>
    public bool IsLive => Status == SessionStatus.Live;

    /// <summary>
    /// Whether the session has been cancelled (the soft, terminal off-ramp of the
    /// session cancel command, CORE-LIFE-010): <see cref="SessionStatus.Cancelled"/>.
    /// </summary>
    public bool IsCancelled => Status == SessionStatus.Cancelled;

    /// <summary>
    /// Whether the session may be started right now: a session may be started only
    /// from <see cref="SessionStatus.Prepared"/>. Lets the application layer branch
    /// without catching <see cref="InvalidSessionStateTransitionException"/>.
    /// </summary>
    public bool CanStart => Status == SessionStatus.Prepared;

    /// <summary>
    /// Whether the session may be ended right now: a session may be ended only from
    /// <see cref="SessionStatus.Live"/> (ending presupposes a live session, since
    /// <c>SessionEnded</c> "ends live timeline", docs/09_EVENT_CATALOG.md). Lets the
    /// application layer branch without catching
    /// <see cref="InvalidSessionStateTransitionException"/>.
    /// </summary>
    public bool CanEnd => Status == SessionStatus.Live;

    /// <summary>
    /// Whether the session may be cancelled right now: a session may be cancelled only
    /// from <see cref="SessionStatus.Prepared"/> — a not-yet-started session
    /// (CORE-LIFE-010). A live, ended or already-cancelled session cannot be cancelled
    /// (a live session must be ended, not cancelled; the others are already terminal).
    /// Lets the application layer branch (returning a 409 conflict) without catching
    /// <see cref="InvalidSessionStateTransitionException"/>.
    /// </summary>
    public bool CanCancel => Status == SessionStatus.Prepared;

    /// <summary>
    /// Creates a new prepared session in the given workspace (owned by the given
    /// organization). The session starts <see cref="SessionStatus.Prepared"/> with
    /// no live timeline yet: <see cref="StartedAt"/> and <see cref="EndedAt"/> are
    /// both <see langword="null"/> (the state behind <c>SessionCreated</c>,
    /// docs/09_EVENT_CATALOG.md).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, or the title violates an
    /// invariant.
    /// </exception>
    public static Session Create(
        Guid organizationId,
        Guid workspaceId,
        string title,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            title?.Trim() ?? string.Empty,
            SessionStatus.Prepared,
            createdAt,
            createdAt,
            startedAt: null,
            endedAt: null);

    /// <summary>
    /// Whether this session belongs to exactly the given organization. Empty ids
    /// match nothing. This is the tenant-boundary check, checked before the
    /// workspace boundary: a session belongs only to the organization it names,
    /// never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this session belongs to exactly the given workspace. Empty ids match
    /// nothing. This is the workspace-boundary check: a session belongs only to the
    /// workspace it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this session is the one identified by the given id WITHIN the given
    /// organization and workspace. All three must match; an empty id matches
    /// nothing. A session with the same id under a different tenant or workspace
    /// (which cannot happen for a single row but guards the caller's intent)
    /// returns <see langword="false"/>: the surrogate id is only ever honored
    /// inside its tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Renames the session's display label. The organization, workspace and id are
    /// immutable, so renaming never moves the session and never changes the tenant
    /// boundary (threat T5). Renaming carries no lifecycle meaning, so it is allowed
    /// in any status (mirrors <c>Participant.Rename</c> on a removed participant).
    /// </summary>
    /// <exception cref="ArgumentException">The new title violates an invariant.</exception>
    public void Rename(string title, DateTimeOffset updatedAt)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (!IsValidTitle(trimmed))
        {
            throw new ArgumentException("Title violates the title invariants.", nameof(title));
        }

        Title = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Starts the session's live timeline: transitions
    /// <see cref="SessionStatus.Prepared"/> -&gt; <see cref="SessionStatus.Live"/>
    /// and stamps <see cref="StartedAt"/> (the state and timestamp behind
    /// <c>SessionStarted</c>, "starts live timeline", docs/09_EVENT_CATALOG.md). The
    /// organization, workspace and id are immutable, so starting never moves the
    /// session (threat T5). Starting an already-live or already-ended session is an
    /// invalid transition, NOT a no-op: a live timeline must reject a re-start as a
    /// genuine state error. Callers that want to avoid the exception can pre-check
    /// <see cref="CanStart"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateTransitionException">
    /// The session is not <see cref="SessionStatus.Prepared"/>.
    /// </exception>
    public void Start(DateTimeOffset at)
    {
        if (!CanStart)
        {
            throw new InvalidSessionStateTransitionException(Id, Status, SessionStatus.Live);
        }

        Status = SessionStatus.Live;
        StartedAt = at.ToUniversalTime();
        UpdatedAt = StartedAt.Value;
    }

    /// <summary>
    /// Ends the session's live timeline: transitions
    /// <see cref="SessionStatus.Live"/> -&gt; <see cref="SessionStatus.Ended"/> and
    /// stamps <see cref="EndedAt"/> (the state and timestamp behind
    /// <c>SessionEnded</c>, "ends live timeline", docs/09_EVENT_CATALOG.md). Ending
    /// is valid only from a live session: ending a session that never started, or an
    /// already-ended session, is an invalid transition, NOT a no-op. The end
    /// timestamp must be at or after <see cref="StartedAt"/> so the live timeline is
    /// well formed. The organization, workspace and id are immutable, so ending
    /// never moves the session (threat T5). Callers that want to avoid the exception
    /// can pre-check <see cref="CanEnd"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateTransitionException">
    /// The session is not <see cref="SessionStatus.Live"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The end timestamp is before <see cref="StartedAt"/>.
    /// </exception>
    public void End(DateTimeOffset at)
    {
        if (!CanEnd)
        {
            throw new InvalidSessionStateTransitionException(Id, Status, SessionStatus.Ended);
        }

        var endedAtUtc = at.ToUniversalTime();

        // A live session always has a StartedAt (Start set it), but guard the
        // null defensively so the invariant is enforced unconditionally.
        if (StartedAt is { } startedAtUtc && endedAtUtc < startedAtUtc)
        {
            throw new ArgumentException(
                "A session cannot end before it started.",
                nameof(at));
        }

        Status = SessionStatus.Ended;
        EndedAt = endedAtUtc;
        UpdatedAt = endedAtUtc;
    }

    /// <summary>
    /// Cancels a not-yet-started session (CORE-LIFE-010): transitions
    /// <see cref="SessionStatus.Prepared"/> -&gt; <see cref="SessionStatus.Cancelled"/>
    /// and stamps <see cref="UpdatedAt"/>. This is a SOFT, terminal transition like
    /// <see cref="Start"/>/<see cref="End"/> (and like <c>Workspace.Archive</c>), NOT a
    /// hard delete: the session row survives so its append-only <c>session_events</c>
    /// and <c>audit_logs</c> history is preserved (the story note: "NEVER delete
    /// append-only session_events or audit_logs - prefer a Cancelled status"). The
    /// session never opened a live timeline, so <see cref="StartedAt"/> and
    /// <see cref="EndedAt"/> stay null and are deliberately not touched. The
    /// organization, workspace and id are immutable, so cancelling never moves the
    /// session (threat T5). Cancelling is valid ONLY from
    /// <see cref="SessionStatus.Prepared"/>: cancelling a session that has started, has
    /// ended, or was already cancelled is an invalid transition, NOT a no-op (a live
    /// session must be ended, not cancelled, and the terminal states are final).
    /// Callers that want to avoid the exception can pre-check <see cref="CanCancel"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateTransitionException">
    /// The session is not <see cref="SessionStatus.Prepared"/>.
    /// </exception>
    public void Cancel(DateTimeOffset at)
    {
        if (!CanCancel)
        {
            throw new InvalidSessionStateTransitionException(Id, Status, SessionStatus.Cancelled);
        }

        Status = SessionStatus.Cancelled;
        UpdatedAt = at.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid title: non-blank, within the length bound
    /// and free of control characters.
    /// </summary>
    public static bool IsValidTitle(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxTitleLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a defined <see cref="SessionStatus"/>. Used to
    /// reject undefined enum values that a cast could otherwise smuggle in.
    /// </summary>
    public static bool IsValidStatus(SessionStatus status) => Enum.IsDefined(status);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: session id,
    /// organization id, workspace id, status and the live-timeline timestamps. The
    /// title is excluded so it cannot leak through logs (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not free-form
    /// metadata content).
    /// </summary>
    public override string ToString()
        => $"Session {Id} org={OrganizationId} ws={WorkspaceId} status={Status} "
            + $"started={(StartedAt is { } started ? started.ToString("O") : "null")} "
            + $"ended={(EndedAt is { } ended ? ended.ToString("O") : "null")}";
}
