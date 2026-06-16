namespace LiveCore.Api.Audit;

/// <summary>
/// An append-only audit log entry. The Audit module owns the "append-only audit log" and the "security
/// event records" (docs/05_MODULE_CONTRACTS.md; csv/database_tables.csv: table <c>audit_logs</c>,
/// module Audit, scope <c>organization</c>, "Append-only audit"). An <see cref="AuditLogEntry"/> is the
/// durable, immutable record of one security-relevant action.
///
/// CORE-VIS-006 introduced this aggregate with a single producer — the visibility reveal command, which
/// records a visibility rule change (<see cref="AuditAction.VisibilityRuleChanged"/>), the
/// docs/07_SECURITY_THREAT_MODEL.md required control "audit creation for visibility changes".
/// CORE-AUD-001 — the generic append-only audit log — makes the creation API GENERIC: the
/// <see cref="Create"/> factory records ANY <see cref="AuditAction"/> as an append-only fact, with every
/// part beyond the tenant and the action optional (an org- or workspace-level action, a user or system
/// actor, an optional governed resource, an optional selected-participant target and an optional
/// before/after state pair). <see cref="ForVisibilityRuleChange"/> is now a thin specialization of
/// <see cref="Create"/>, so the visibility producer is unchanged and visibility logic is not duplicated.
///
/// APPEND-ONLY (docs/10_DATABASE_SCHEMA.md: "audit logs are append-only"). The aggregate is fully
/// immutable: every property is get-only and there is NO state-transition method, no setter and no
/// delete on the repository. An audit fact, once written, is never updated or removed — that
/// immutability is what makes the log trustworthy as a security record.
///
/// RECORDED FACTS, NOT LIVE REFERENCES. The reference columns (<see cref="WorkspaceId"/>,
/// <see cref="ActorUserProfileId"/>, <see cref="ResourceId"/>, <see cref="TargetParticipantId"/>)
/// capture the ids AS THEY WERE at the moment of the action. They are deliberately NOT database
/// foreign keys (see <c>AuditLogConfiguration</c>): an audit trail must SURVIVE the later deletion of
/// the things it references and must never be cascade-erased, so the trail cannot be coupled by
/// referential integrity to mutable business rows. The single exception is <see cref="OrganizationId"/>,
/// the tenant boundary, which IS a foreign key so isolation is enforced at the row level (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md) — a tenant teardown removes its audit log, but nothing finer-grained
/// can.
///
/// TENANT SCOPE (and PLATFORM-LEVEL facts). The audit log is tenant-scoped (csv/database_tables.csv scope
/// <c>organization</c>), so a tenant-scoped entry carries <see cref="OrganizationId"/>; the documented critical
/// index is <c>audit_logs(organization_id, created_at)</c> (docs/10_DATABASE_SCHEMA.md). CORE-SPEC-002 makes the
/// organization OPTIONAL: a PLATFORM-LEVEL audit fact — a deployment-spanning, not tenant-scoped security event
/// such as a purchase grant/revocation, a purchase verification or a store notification, which carries no
/// organization (a purchase is named globally and a user's premium follows the user, not a tenant — docs/21) —
/// records a <see langword="null"/> organization. Such facts are append-only but stand OUTSIDE the per-tenant
/// tamper-evident hash chain (CORE-SEC-003), whose spine is the per-tenant append sequence; their integrity
/// posture is the same append-only + DB-level <c>REVOKE</c> the established <c>purchase_events</c> monetization
/// trail has (docs/13). A visibility change is also workspace-scoped, so the visibility producer always sets
/// <see cref="WorkspaceId"/>; the column is nullable so an organization-level generic action (CORE-AUD-001)
/// needs no schema change.
///
/// NO SENSITIVE CONTENT (threat T7). Every field is an identifier, an enum or a generic state NAME
/// (Hidden/Visible) — never free-form scene/content body — so <see cref="ToString"/> is safe for
/// structured logs, and the audit log itself never stores the revealed content, only the fact that a
/// visibility change happened, to whom and from which state to which.
///
/// GENERIC, EXTENSIBLE SHAPE. The columns are generic on purpose (a generic action, an optional
/// resource reference, an optional before/after state pair) so new actions extend this table by being
/// recorded through the generic <see cref="Create"/> factory, not by reshaping it. CORE-AUD-001 adds
/// that generic creation API; the audit query permissions story (CORE-AUD-005) adds the read-side
/// authorization — <see cref="AuditQueryPolicy"/>, the fail-closed "View audit log" decision
/// (Owner/Admin/Auditor, docs/06_AUTHORIZATION_MATRIX.md) — and the safe <see cref="AuditLogEntryView"/>
/// read view. No HTTP read route is built (csv/api_routes.csv defines none): the audit log is written as a
/// side effect of already-authorized commands and read only through that authorized policy + projection.
/// </summary>
public sealed class AuditLogEntry
{
    /// <summary>Maximum stored length of a generic before/after state value (an enum name).</summary>
    public const int MaxStateLength = 32;

    /// <summary>Maximum stored length of the generic resource-type name (an enum name).</summary>
    public const int MaxResourceTypeLength = 32;

    private AuditLogEntry(
        Guid id,
        Guid? organizationId,
        Guid? workspaceId,
        AuditAction action,
        Guid? actorUserProfileId,
        string? resourceType,
        Guid? resourceId,
        Guid? targetParticipantId,
        string? previousState,
        string? newState,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Audit log entry id must not be empty.", nameof(id));
        }

        // A null organization is a PLATFORM-LEVEL audit fact (CORE-SPEC-002): a deployment-spanning, not
        // tenant-scoped security event such as a purchase grant/revocation or a store notification, which carries
        // no organization (a purchase is named globally and a user's premium follows the user, not a tenant —
        // docs/21). A SET organization must address a real tenant, so an explicit empty id is rejected rather than
        // stored as a misleading "all zeros" reference.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id must not be empty; pass null for a platform-level entry.",
                nameof(organizationId));
        }

        // A null workspace id means an org-level event; a SET value must address a real workspace. An
        // empty (but non-null) id can never address a workspace, so it is rejected rather than stored
        // as a misleading "all zeros" reference.
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Workspace id must not be empty; pass null for an organization-level entry.",
                nameof(workspaceId));
        }

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Audit action is not defined.");
        }

        // A null actor means a system-generated event; a SET actor must be a real user profile id.
        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "Actor user profile id must not be empty; pass null for a system event.",
                nameof(actorUserProfileId));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Resource id must not be empty; pass null when the entry has no resource.",
                nameof(resourceId));
        }

        // A resource reference is the (type, id) PAIR or neither: a generic action with no governed
        // resource records both as null, a resource-scoped action records both. A half-set reference
        // (one without the other) can never address a resource, so it is rejected rather than stored.
        if (resourceType is null != (resourceId is null))
        {
            throw new ArgumentException(
                "Resource type and resource id must be supplied together or both omitted.",
                nameof(resourceType));
        }

        if (resourceType is not null && string.IsNullOrWhiteSpace(resourceType))
        {
            throw new ArgumentException("Resource type must not be blank when provided.", nameof(resourceType));
        }

        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null for an audience-wide entry.",
                nameof(targetParticipantId));
        }

        // The before/after state is OPTIONAL: a generic action need not be a state transition (a session
        // start or an invite has no audit state pair). A SET value must still be meaningful, so a
        // whitespace-only state is rejected rather than stored as a blank string.
        if (previousState is not null && string.IsNullOrWhiteSpace(previousState))
        {
            throw new ArgumentException("Previous state must not be blank when provided.", nameof(previousState));
        }

        if (newState is not null && string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be blank when provided.", nameof(newState));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        Action = action;
        ActorUserProfileId = actorUserProfileId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        TargetParticipantId = targetParticipantId;
        PreviousState = previousState;
        NewState = newState;
        // Normalized to UTC so the persisted timestamptz value is offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private AuditLogEntry()
    {
    }

    /// <summary>
    /// Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md). Its
    /// timestamp component is derived from <see cref="CreatedAt"/> (the event time), so ordering by this
    /// id is chronological by when the action happened.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the entry: the organization the audited action happened in (the
    /// <c>organization_id</c> foreign key to <c>organizations</c>), or <see langword="null"/> for a
    /// PLATFORM-LEVEL audit fact (CORE-SPEC-002) — a deployment-spanning, not tenant-scoped security event such
    /// as a purchase grant/revocation, a purchase verification or a store notification, which carries no
    /// organization (docs/21). Part of the documented critical index <c>audit_logs(organization_id, created_at)</c>;
    /// the tenant-scoped reads filter by a concrete organization id, so a platform fact (null tenant) is never
    /// returned through any tenant's id and one tenant's records are never returned through another's (threat T5).
    /// </summary>
    public Guid? OrganizationId { get; }

    /// <summary>
    /// The workspace the audited action happened in, or <see langword="null"/> for an organization-level
    /// event. A visibility rule change is always workspace-scoped, so this story always sets it. A
    /// RECORDED FACT, not a foreign key (see the type summary).
    /// </summary>
    public Guid? WorkspaceId { get; }

    /// <summary>The kind of action this entry records (docs/09_EVENT_CATALOG.md).</summary>
    public AuditAction Action { get; }

    /// <summary>
    /// The user profile that performed the action (docs/09_EVENT_CATALOG.md <c>createdBy</c>), or
    /// <see langword="null"/> for a system-generated event. A RECORDED FACT, not a foreign key.
    /// </summary>
    public Guid? ActorUserProfileId { get; }

    /// <summary>
    /// The generic kind of the resource the action concerned (Scene/ContentBlock/Entity for a
    /// visibility change), or <see langword="null"/> when the entry has no resource. Stored as a generic
    /// NAME string so the Audit module stays decoupled from the Visibility module's enum.
    /// </summary>
    public string? ResourceType { get; }

    /// <summary>
    /// The id of the resource the action concerned, or <see langword="null"/>. A RECORDED FACT, not a
    /// foreign key (the reference is polymorphic across scenes/content_blocks/entities, exactly like
    /// <c>visibility_rules.resource_id</c>).
    /// </summary>
    public Guid? ResourceId { get; }

    /// <summary>
    /// The participant a selected-participant visibility change targeted (CORE-VIS-005), or
    /// <see langword="null"/> for an audience-wide change. A RECORDED FACT, not a foreign key.
    /// </summary>
    public Guid? TargetParticipantId { get; }

    /// <summary>
    /// The generic state BEFORE the action (the prior visibility name, Hidden/Visible), or
    /// <see langword="null"/> when there was no prior state (for example a brand-new visibility rule).
    /// </summary>
    public string? PreviousState { get; }

    /// <summary>
    /// The generic state AFTER the action (the new visibility name, e.g. Visible), or
    /// <see langword="null"/> when the action is not a state transition (a generic action such as a
    /// session start or a member invite has no audit state pair). Always set by
    /// <see cref="ForVisibilityRuleChange"/>, which records a real state change.
    /// </summary>
    public string? NewState { get; }

    /// <summary>When the audited action happened (UTC). Part of the critical index with the tenant.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// The per-tenant APPEND sequence number of this entry — a gap-free, strictly monotonic value handed out by
    /// the <c>audit_log_sequences</c> allocator when the entry is appended (CORE-SEC-003), and the spine the
    /// tamper-evident hash chain is linked along. It is distinct from <see cref="Id"/> (which orders by EVENT
    /// time and may be recorded out of append order); the chain follows append order, so it follows this
    /// sequence. Set once by <see cref="Seal"/> at append time (its default zero means an unsealed, not-yet-
    /// appended entry); the unique index <c>audit_logs(organization_id, sequence)</c> is the integrity backstop
    /// guaranteeing no two entries of a tenant ever share a sequence.
    /// </summary>
    public long Sequence { get; private set; }

    /// <summary>
    /// The chain hash of the entry that PRECEDES this one in its tenant's append-only chain
    /// (<see cref="EntryHash"/> of the entry at <see cref="Sequence"/> − 1), or <see langword="null"/> for the
    /// chain's genesis entry (CORE-SEC-003). This is the prev-hash link that makes the log tamper-evident: a
    /// deletion or reordering breaks the linkage <see cref="AuditLogChain.Verify"/> checks. Set once by
    /// <see cref="Seal"/> at append time.
    /// </summary>
    public string? PreviousHash { get; private set; }

    /// <summary>
    /// This entry's own chain hash — a SHA-256 over its recorded fields and its <see cref="PreviousHash"/>
    /// (<see cref="AuditLogChain.ComputeEntryHash"/>), so altering any persisted field is detectable
    /// (CORE-SEC-003). It is <see langword="null"/> ONLY for a legacy entry written before this hardening landed
    /// (such entries predate the chain and are not verified); every entry appended through
    /// <see cref="Seal"/> carries a non-null hash. Set once by <see cref="Seal"/> at append time.
    /// </summary>
    public string? EntryHash { get; private set; }

    /// <summary>
    /// Records a generic security-relevant action as an append-only audit fact — the generic creation API
    /// of the append-only audit log (CORE-AUD-001). Any module records a security event through this one
    /// factory (then <see cref="IAuditLogRepository.AppendAsync"/>); <see cref="ForVisibilityRuleChange"/>
    /// is the visibility producer's specialization of it.
    ///
    /// Every part beyond the tenant and the action is OPTIONAL, so the log is genuinely generic:
    /// <paramref name="workspaceId"/> is <see langword="null"/> for an organization-level action;
    /// <paramref name="actorUserProfileId"/> is <see langword="null"/> for a system action; the resource
    /// reference (<paramref name="resourceType"/>, <paramref name="resourceId"/>) is supplied as a PAIR
    /// or omitted entirely; <paramref name="targetParticipantId"/> is <see langword="null"/> for an
    /// audience-wide action; and <paramref name="previousState"/>/<paramref name="newState"/> are the
    /// optional generic before/after state NAMES of a transition (a non-transition action records
    /// neither). Every value is an identifier, an enum or a generic state name — never free-form content
    /// (threat T7). The entry is immutable; there is no update or delete (append-only,
    /// docs/10_DATABASE_SCHEMA.md).
    /// </summary>
    /// <param name="organizationId">The tenant the action happened in, or <see langword="null"/> for a platform-level (deployment-spanning) action.</param>
    /// <param name="workspaceId">The workspace the action happened in, or <see langword="null"/> for an organization-level action.</param>
    /// <param name="action">The kind of security-relevant action (required, must be a defined <see cref="AuditAction"/>).</param>
    /// <param name="actorUserProfileId">The user who performed the action, or <see langword="null"/> for a system action.</param>
    /// <param name="resourceType">The governed resource kind name, or <see langword="null"/> when the action concerns no resource.</param>
    /// <param name="resourceId">The governed resource id, paired with <paramref name="resourceType"/> or both <see langword="null"/>.</param>
    /// <param name="targetParticipantId">The selected participant, or <see langword="null"/> for an audience-wide action.</param>
    /// <param name="previousState">The generic state name before the action, or <see langword="null"/>.</param>
    /// <param name="newState">The generic state name after the action, or <see langword="null"/> when the action is not a transition.</param>
    /// <param name="createdAt">When the action happened.</param>
    /// <exception cref="ArgumentException">
    /// The organization id is explicitly empty (pass null for a platform-level entry), a SET optional id is
    /// explicitly empty, the resource reference is half-set, or a SET resource type / state value is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The action is not a defined <see cref="AuditAction"/>.</exception>
    public static AuditLogEntry Create(
        Guid? organizationId,
        Guid? workspaceId,
        AuditAction action,
        Guid? actorUserProfileId,
        string? resourceType,
        Guid? resourceId,
        Guid? targetParticipantId,
        string? previousState,
        string? newState,
        DateTimeOffset createdAt)
        => new(
            // Derive the UUIDv7 timestamp from the event time, not the wall clock at construction, so the
            // time-ordered surrogate id orders chronologically by when the action happened — which is what
            // ListByOrganizationAsync relies on when it orders by id (two entries created in the same wall-
            // clock millisecond but at different event times would otherwise tie-break on UUIDv7's random
            // bits and read back out of order).
            Guid.CreateVersion7(createdAt),
            organizationId,
            workspaceId,
            action,
            actorUserProfileId,
            resourceType,
            resourceId,
            targetParticipantId,
            previousState,
            newState,
            createdAt);

    /// <summary>
    /// Records a visibility rule change (<see cref="AuditAction.VisibilityRuleChanged"/>) — the audit
    /// fact written by the Visibility module's reveal command when a resource's audience visibility
    /// actually changes (CORE-VIS-006). A thin specialization of <see cref="Create"/> that pins the
    /// action and applies the visibility producer's stricter contract (the workspace, actor, resource
    /// and new state are all REQUIRED for a real visibility change, where the generic factory leaves them
    /// optional). The caller (the reveal command, the only producer) supplies the already-resolved
    /// tenant, workspace, the authenticated actor, the governed resource, the optional
    /// selected-participant target and the before/after visibility state NAMES (passed as generic
    /// strings so this module does not depend on the Visibility enum).
    /// </summary>
    /// <param name="organizationId">The tenant the change happened in (required).</param>
    /// <param name="workspaceId">The workspace the changed rule belongs to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the reveal (required; the audited actor).</param>
    /// <param name="resourceType">The governed resource kind name (Scene/ContentBlock/Entity).</param>
    /// <param name="resourceId">The governed resource id.</param>
    /// <param name="targetParticipantId">
    /// The selected participant for a private reveal, or <see langword="null"/> for an audience-wide
    /// change.
    /// </param>
    /// <param name="previousState">
    /// The visibility state name before the change, or <see langword="null"/> when no rule existed before.
    /// </param>
    /// <param name="newState">The visibility state name after the change (required).</param>
    /// <param name="createdAt">When the change happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, an optional id is explicitly empty, or the resource type / new state is blank.
    /// </exception>
    public static AuditLogEntry ForVisibilityRuleChange(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string resourceType,
        Guid resourceId,
        Guid? targetParticipantId,
        string? previousState,
        string newState,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new ArgumentException("Resource type must not be empty.", nameof(resourceType));
        }

        if (resourceId == Guid.Empty)
        {
            throw new ArgumentException("Resource id must not be empty.", nameof(resourceId));
        }

        // A real visibility change always has a resulting state, so the visibility producer requires it
        // even though the generic factory leaves it optional.
        if (string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be empty.", nameof(newState));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.VisibilityRuleChanged,
            actorUserProfileId,
            resourceType,
            resourceId,
            targetParticipantId,
            previousState,
            newState,
            createdAt);
    }

    /// <summary>
    /// Records a session start (<see cref="AuditAction.SessionStarted"/>) — the audit fact written when an
    /// authorized host starts a session, opening its live timeline (CORE-EVT-001). It is the start
    /// counterpart of <see cref="ForSessionCancellation"/> and, like it, a thin specialization of
    /// <see cref="Create"/> that pins the action and applies the lifecycle producer's stricter contract: the
    /// tenant, the workspace the session belongs to, the authenticated actor (the host who started the
    /// session) and the started session resource (its generic kind name and surrogate id) are all REQUIRED,
    /// where the generic factory leaves them optional. A start is a real STATE TRANSITION (the session
    /// survives), so it records the before/after status NAMES (e.g. <c>Prepared</c> -&gt; <c>Live</c>),
    /// exactly as <see cref="ForVisibilityRuleChange"/> records a visibility transition. The session is both
    /// the scope (<paramref name="workspaceId"/> is the session's workspace) and the governed resource (its
    /// id), because the action is performed ON the session. The resource kind and the state names are passed
    /// as generic strings so the Audit module does not depend on the Sessions module's types. Every value is
    /// an identifier, an enum or a generic state name — never free-form content (threat T7).
    /// </summary>
    /// <param name="organizationId">The tenant the start happened in (required).</param>
    /// <param name="workspaceId">The workspace the started session belongs to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the start (required; the audited actor).</param>
    /// <param name="sessionResourceType">The started session's generic kind name (e.g. Session).</param>
    /// <param name="sessionId">The started session's surrogate id.</param>
    /// <param name="previousState">The lifecycle status name before the start (e.g. Prepared; required).</param>
    /// <param name="newState">The lifecycle status name after the start (e.g. Live; required).</param>
    /// <param name="createdAt">When the start happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the session resource type / a state name is blank.
    /// </exception>
    public static AuditLogEntry ForSessionStart(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string sessionResourceType,
        Guid sessionId,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
        => ForSessionLifecycleTransition(
            AuditAction.SessionStarted,
            organizationId,
            workspaceId,
            actorUserProfileId,
            sessionResourceType,
            sessionId,
            previousState,
            newState,
            createdAt);

    /// <summary>
    /// Records a session end (<see cref="AuditAction.SessionEnded"/>) — the audit fact written when an
    /// authorized host ends a session, closing its live timeline (CORE-EVT-001). It is the end counterpart of
    /// <see cref="ForSessionStart"/> and records the same shape: a real STATE TRANSITION carrying the
    /// before/after status NAMES (e.g. <c>Live</c> -&gt; <c>Ended</c>), the session as both the scope and the
    /// governed resource, and the host who ended it as the actor. Every value is an identifier, an enum or a
    /// generic state name — never free-form content (threat T7).
    /// </summary>
    /// <param name="organizationId">The tenant the end happened in (required).</param>
    /// <param name="workspaceId">The workspace the ended session belongs to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the end (required; the audited actor).</param>
    /// <param name="sessionResourceType">The ended session's generic kind name (e.g. Session).</param>
    /// <param name="sessionId">The ended session's surrogate id.</param>
    /// <param name="previousState">The lifecycle status name before the end (e.g. Live; required).</param>
    /// <param name="newState">The lifecycle status name after the end (e.g. Ended; required).</param>
    /// <param name="createdAt">When the end happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the session resource type / a state name is blank.
    /// </exception>
    public static AuditLogEntry ForSessionEnd(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string sessionResourceType,
        Guid sessionId,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
        => ForSessionLifecycleTransition(
            AuditAction.SessionEnded,
            organizationId,
            workspaceId,
            actorUserProfileId,
            sessionResourceType,
            sessionId,
            previousState,
            newState,
            createdAt);

    /// <summary>
    /// Shared specialization behind <see cref="ForSessionStart"/>, <see cref="ForSessionEnd"/> and
    /// <see cref="ForSessionCancellation"/>: a session lifecycle STATE TRANSITION recorded as an
    /// append-only audit fact. The three differ only in the <paramref name="action"/> they pin; each is a
    /// real before/after status transition on the surviving session row (never a deletion), so the workspace,
    /// actor, session resource and both status names are all required here even though the generic
    /// <see cref="Create"/> factory leaves them optional.
    /// </summary>
    private static AuditLogEntry ForSessionLifecycleTransition(
        AuditAction action,
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string sessionResourceType,
        Guid sessionId,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(sessionResourceType))
        {
            throw new ArgumentException("Session resource type must not be empty.", nameof(sessionResourceType));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        // A lifecycle transition always has both a before and an after status, so the producer requires them
        // even though the generic factory leaves the state pair optional.
        if (string.IsNullOrWhiteSpace(previousState))
        {
            throw new ArgumentException("Previous state must not be empty.", nameof(previousState));
        }

        if (string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be empty.", nameof(newState));
        }

        return Create(
            organizationId,
            workspaceId,
            action,
            actorUserProfileId,
            sessionResourceType,
            sessionId,
            targetParticipantId: null,
            previousState: previousState,
            newState: newState,
            createdAt);
    }

    /// <summary>
    /// Records a member removal (<see cref="AuditAction.MemberRemoved"/>) — the audit fact written when an
    /// authorized admin removes a workspace or organization member, revoking the subject's access
    /// (CORE-LIFE-001). A thin specialization of <see cref="Create"/> that pins the action and applies the
    /// removal producer's stricter contract: the tenant, the authenticated actor (the admin who removed the
    /// member), the removed member resource (its generic kind name and surrogate id) and the removed role
    /// are all REQUIRED, where the generic factory leaves them optional. The removed role is recorded as the
    /// PREVIOUS state — the access that was revoked — and there is no new state, because a removal is a
    /// deletion rather than a transition (so <paramref name="removedRole"/> maps to <c>previousState</c> and
    /// <c>newState</c> is null). The workspace is set for a workspace member removal and null for an
    /// organization-level one. The role is passed as a generic NAME string so the Audit module does not
    /// depend on the Organizations role enum, exactly like <see cref="ForVisibilityRuleChange"/> takes
    /// visibility state names as strings. Every value is an identifier or a generic name — never free-form
    /// content (threat T7) — and the audit row outlives the now-deleted membership it references because the
    /// reference is a recorded fact, not a foreign key (see the type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the removal happened in (required).</param>
    /// <param name="workspaceId">
    /// The workspace the removed membership belonged to for a workspace member removal, or
    /// <see langword="null"/> for an organization member removal (organization-level).
    /// </param>
    /// <param name="actorUserProfileId">The admin who performed the removal (required; the audited actor).</param>
    /// <param name="memberResourceType">The removed membership's generic kind name (e.g. WorkspaceMember / OrganizationMember).</param>
    /// <param name="memberId">The removed membership's surrogate id.</param>
    /// <param name="removedRole">The generic role NAME the removed member held — the revoked access (required).</param>
    /// <param name="createdAt">When the removal happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, an optional id is explicitly empty, or the member resource type / removed role
    /// is blank.
    /// </exception>
    public static AuditLogEntry ForMemberRemoval(
        Guid organizationId,
        Guid? workspaceId,
        Guid actorUserProfileId,
        string memberResourceType,
        Guid memberId,
        string removedRole,
        DateTimeOffset createdAt)
    {
        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(memberResourceType))
        {
            throw new ArgumentException("Member resource type must not be empty.", nameof(memberResourceType));
        }

        if (memberId == Guid.Empty)
        {
            throw new ArgumentException("Member id must not be empty.", nameof(memberId));
        }

        // A real removal always revokes a known role, so the producer requires it (recorded as the previous
        // state) even though the generic factory leaves the state pair optional.
        if (string.IsNullOrWhiteSpace(removedRole))
        {
            throw new ArgumentException("Removed role must not be empty.", nameof(removedRole));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.MemberRemoved,
            actorUserProfileId,
            memberResourceType,
            memberId,
            targetParticipantId: null,
            previousState: removedRole,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records a workspace invitation redemption (<see cref="AuditAction.MemberJoined"/>) — the audit fact
    /// written when an authenticated caller redeems a scoped invitation and gains the granted workspace
    /// membership (CORE-WS-006). It is the inverse of <see cref="ForMemberRemoval"/>: a thin specialization
    /// of <see cref="Create"/> that pins the action and applies the redemption producer's stricter contract —
    /// the tenant, the workspace the membership was granted in, the authenticated actor (the caller who
    /// redeemed the token and BECAME the member, the bearer grant) and the created membership resource (its
    /// generic kind name and surrogate id) are all REQUIRED, where the generic factory leaves them optional. A
    /// redemption GRANTS access, so — symmetrically to a removal recording the revoked role as the previous
    /// state — it records the granted role as the NEW state, with no previous state (the subject held no
    /// membership before). The role is passed as a generic NAME string so the Audit module does not depend on
    /// the Workspaces role enum. Every value is an identifier or a generic name — never the invited email, the
    /// token or any free-form content (threats T6/T7).
    /// </summary>
    /// <param name="organizationId">The tenant the redemption happened in (required).</param>
    /// <param name="workspaceId">The workspace the membership was granted in (required for this action).</param>
    /// <param name="actorUserProfileId">The caller who redeemed the token and became the member (required; the audited actor).</param>
    /// <param name="memberResourceType">The created membership's generic kind name (e.g. WorkspaceMember).</param>
    /// <param name="memberId">The created membership's surrogate id.</param>
    /// <param name="grantedRole">The generic role NAME the new member was granted — the access conferred (required).</param>
    /// <param name="createdAt">When the redemption happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the member resource type / granted role is blank.
    /// </exception>
    public static AuditLogEntry ForMemberJoined(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string memberResourceType,
        Guid memberId,
        string grantedRole,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(memberResourceType))
        {
            throw new ArgumentException("Member resource type must not be empty.", nameof(memberResourceType));
        }

        if (memberId == Guid.Empty)
        {
            throw new ArgumentException("Member id must not be empty.", nameof(memberId));
        }

        // A real redemption always grants a known role, so the producer requires it (recorded as the new
        // state) even though the generic factory leaves the state pair optional.
        if (string.IsNullOrWhiteSpace(grantedRole))
        {
            throw new ArgumentException("Granted role must not be empty.", nameof(grantedRole));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.MemberJoined,
            actorUserProfileId,
            memberResourceType,
            memberId,
            targetParticipantId: null,
            previousState: null,
            newState: grantedRole,
            createdAt);
    }

    /// <summary>
    /// Records a workspace invitation revocation (<see cref="AuditAction.MemberInvitationRevoked"/>) — the audit
    /// fact written when an authorized Owner/Admin revokes a pending workspace invitation so its scoped token can
    /// never be redeemed (CORE-WS-007). It is the take-back counterpart of <see cref="ForMemberJoined"/> (the
    /// redemption path) and, like <see cref="ForWorkspaceArchive"/>, a thin specialization of
    /// <see cref="Create"/> that pins the action and applies the revoke producer's stricter contract: the tenant,
    /// the workspace the invitation belonged to, the authenticated actor (the admin who revoked it) and the
    /// revoked invitation resource (its generic kind name and surrogate id) are all REQUIRED, where the generic
    /// factory leaves them optional. Unlike the deletion factories, a revoke is a real STATE TRANSITION (the
    /// invitation row survives so its audit history is preserved), so it records the before/after status NAMES
    /// (e.g. <c>Pending</c> -&gt; <c>Revoked</c>), exactly as <see cref="ForWorkspaceArchive"/> records the
    /// archive transition. The invitation is both the scope (<paramref name="workspaceId"/> is the invitation's
    /// workspace) and the governed resource (its id), because the action is performed ON the invitation. The
    /// resource kind and the state names are passed as generic strings so the Audit module does not depend on the
    /// Workspaces module's types. Every value is an identifier, an enum or a generic state name — never the
    /// invited email, the token or any free-form content (threats T6/T7) — and the audit row outlives any later
    /// change to the invitation it references because the reference is a recorded fact, not a foreign key (see the
    /// type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the revocation happened in (required).</param>
    /// <param name="workspaceId">The workspace the revoked invitation belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The admin who performed the revocation (required; the audited actor).</param>
    /// <param name="invitationResourceType">The revoked invitation's generic kind name (e.g. WorkspaceInvitation).</param>
    /// <param name="invitationId">The revoked invitation's surrogate id.</param>
    /// <param name="previousState">The lifecycle status name before the revoke (e.g. Pending; required).</param>
    /// <param name="newState">The lifecycle status name after the revoke (e.g. Revoked; required).</param>
    /// <param name="createdAt">When the revocation happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the invitation resource type / a state name is blank.
    /// </exception>
    public static AuditLogEntry ForMemberInvitationRevoked(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string invitationResourceType,
        Guid invitationId,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(invitationResourceType))
        {
            throw new ArgumentException("Invitation resource type must not be empty.", nameof(invitationResourceType));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id must not be empty.", nameof(invitationId));
        }

        // A revoke is a real state transition, so both before and after status names are required even though
        // the generic factory leaves the state pair optional.
        if (string.IsNullOrWhiteSpace(previousState))
        {
            throw new ArgumentException("Previous state must not be empty.", nameof(previousState));
        }

        if (string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be empty.", nameof(newState));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.MemberInvitationRevoked,
            actorUserProfileId,
            invitationResourceType,
            invitationId,
            targetParticipantId: null,
            previousState: previousState,
            newState: newState,
            createdAt);
    }

    /// <summary>
    /// Records an entity creation (<see cref="AuditAction.EntityCreated"/>) — the audit fact written when an
    /// authorized authoring role creates a new entity in a workspace (CORE-ENT-006). It is the authoring
    /// counterpart of <see cref="ForEntityDeletion"/> and, like it, a thin specialization of
    /// <see cref="Create"/> that pins the action and applies the producer's stricter contract: the tenant,
    /// the workspace the entity belongs to, the authenticated actor (the authoring role who created the
    /// entity) and the created entity resource (its generic kind name and surrogate id) are all REQUIRED,
    /// where the generic factory leaves them optional. A creation is the BIRTH of a resource rather than a
    /// transition, and an entity has no lifecycle state, so there is NO before/after state pair (both null).
    /// The resource kind is passed as a generic NAME string (e.g. <c>Entity</c>) so the Audit module does not
    /// depend on the Entities module's types, exactly like <see cref="ForVisibilityRuleChange"/> takes
    /// visibility state names as strings. Every value is an identifier or a generic name — never the entity's
    /// host-/template-supplied name or attribute values or any free-form content (threat T7).
    /// </summary>
    /// <param name="organizationId">The tenant the creation happened in (required).</param>
    /// <param name="workspaceId">The workspace the created entity belongs to (required for this action).</param>
    /// <param name="actorUserProfileId">The authoring role who performed the creation (required; the audited actor).</param>
    /// <param name="entityResourceType">The created entity's generic kind name (e.g. Entity).</param>
    /// <param name="entityId">The created entity's surrogate id.</param>
    /// <param name="createdAt">When the creation happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the entity resource type is blank.
    /// </exception>
    public static AuditLogEntry ForEntityCreation(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string entityResourceType,
        Guid entityId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(entityResourceType))
        {
            throw new ArgumentException("Entity resource type must not be empty.", nameof(entityResourceType));
        }

        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.EntityCreated,
            actorUserProfileId,
            entityResourceType,
            entityId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records an entity-type definition (<see cref="AuditAction.EntityTypeCreated"/>) — the audit fact written
    /// when an authorized authoring role defines a new entity TYPE in a workspace (CORE-ENT-007). It is the
    /// type-definition counterpart of <see cref="ForEntityCreation"/> and, like it, a thin specialization of
    /// <see cref="Create"/> that pins the action and applies the producer's stricter contract: the tenant, the
    /// workspace the type belongs to, the authenticated actor (the authoring role who defined the type) and the
    /// created entity-type resource (its generic kind name and surrogate id) are all REQUIRED, where the generic
    /// factory leaves them optional. A definition is the BIRTH of a resource rather than a transition, and an
    /// entity type has no lifecycle state, so there is NO before/after state pair (both null). The resource kind
    /// is passed as a generic NAME string (e.g. <c>EntityType</c>) so the Audit module does not depend on the
    /// Entities module's types, exactly like <see cref="ForVisibilityRuleChange"/> takes visibility state names
    /// as strings. Every value is an identifier or a generic name — never the type's host-/template-supplied key,
    /// display name or attribute schema or any free-form content (threat T7).
    /// </summary>
    /// <param name="organizationId">The tenant the definition happened in (required).</param>
    /// <param name="workspaceId">The workspace the created entity type belongs to (required for this action).</param>
    /// <param name="actorUserProfileId">The authoring role who defined the type (required; the audited actor).</param>
    /// <param name="entityTypeResourceType">The created entity type's generic kind name (e.g. EntityType).</param>
    /// <param name="entityTypeId">The created entity type's surrogate id.</param>
    /// <param name="createdAt">When the definition happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the entity-type resource type is blank.
    /// </exception>
    public static AuditLogEntry ForEntityTypeCreation(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string entityTypeResourceType,
        Guid entityTypeId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(entityTypeResourceType))
        {
            throw new ArgumentException("Entity type resource type must not be empty.", nameof(entityTypeResourceType));
        }

        if (entityTypeId == Guid.Empty)
        {
            throw new ArgumentException("Entity type id must not be empty.", nameof(entityTypeId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.EntityTypeCreated,
            actorUserProfileId,
            entityTypeResourceType,
            entityTypeId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records an entity deletion (<see cref="AuditAction.EntityDeleted"/>) — the audit fact written when
    /// an authorized host deletes an entity, removing it and its dependent edges, visibility rules and
    /// asset links (CORE-LIFE-003). A thin specialization of <see cref="Create"/> that pins the action and
    /// applies the deletion producer's stricter contract: the tenant, the workspace the entity belonged to,
    /// the authenticated actor (the host who deleted the entity) and the deleted entity resource (its
    /// generic kind name and surrogate id) are all REQUIRED, where the generic factory leaves them
    /// optional. A deletion is a removal rather than a transition, and an entity has no lifecycle state, so
    /// there is NO before/after state pair (both null) — unlike <see cref="ForMemberRemoval"/>, which
    /// records the revoked role as the previous state. The resource kind is passed as a generic NAME string
    /// (e.g. <c>Entity</c>) so the Audit module does not depend on the Entities module's types, exactly like
    /// <see cref="ForVisibilityRuleChange"/> takes visibility state names as strings. Every value is an
    /// identifier or a generic name — never free-form content (threat T7) — and the audit row outlives the
    /// now-deleted entity it references because the reference is a recorded fact, not a foreign key (see the
    /// type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the deletion happened in (required).</param>
    /// <param name="workspaceId">The workspace the deleted entity belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the deletion (required; the audited actor).</param>
    /// <param name="entityResourceType">The deleted entity's generic kind name (e.g. Entity).</param>
    /// <param name="entityId">The deleted entity's surrogate id.</param>
    /// <param name="createdAt">When the deletion happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the entity resource type is blank.
    /// </exception>
    public static AuditLogEntry ForEntityDeletion(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string entityResourceType,
        Guid entityId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(entityResourceType))
        {
            throw new ArgumentException("Entity resource type must not be empty.", nameof(entityResourceType));
        }

        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.EntityDeleted,
            actorUserProfileId,
            entityResourceType,
            entityId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records a content block deletion (<see cref="AuditAction.ContentBlockDeleted"/>) — the audit fact
    /// written when an authorized host deletes a content block from a scene, removing it (together with its
    /// inline revision history) and its dependent visibility rules and asset links (CORE-LIFE-004). It is the
    /// content-block counterpart of <see cref="ForEntityDeletion"/> and, like it, a thin specialization of
    /// <see cref="Create"/> that pins the action and applies the deletion producer's stricter contract: the
    /// tenant, the workspace the content block belonged to, the authenticated actor (the host who deleted the
    /// content block) and the deleted content-block resource (its generic kind name and surrogate id) are all
    /// REQUIRED, where the generic factory leaves them optional. A deletion is a removal rather than a
    /// transition, and a content block has no lifecycle state, so there is NO before/after state pair (both
    /// null). The resource kind is passed as a generic NAME string (e.g. <c>ContentBlock</c>) so the Audit
    /// module does not depend on the Content module's types, exactly like <see cref="ForVisibilityRuleChange"/>
    /// takes visibility state names as strings. Every value is an identifier or a generic name — never
    /// free-form content (threat T7) — and the audit row outlives the now-deleted content block it references
    /// because the reference is a recorded fact, not a foreign key (see the type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the deletion happened in (required).</param>
    /// <param name="workspaceId">The workspace the deleted content block belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the deletion (required; the audited actor).</param>
    /// <param name="contentBlockResourceType">The deleted content block's generic kind name (e.g. ContentBlock).</param>
    /// <param name="contentBlockId">The deleted content block's surrogate id.</param>
    /// <param name="createdAt">When the deletion happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the content-block resource type is blank.
    /// </exception>
    public static AuditLogEntry ForContentBlockDeletion(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string contentBlockResourceType,
        Guid contentBlockId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(contentBlockResourceType))
        {
            throw new ArgumentException("Content block resource type must not be empty.", nameof(contentBlockResourceType));
        }

        if (contentBlockId == Guid.Empty)
        {
            throw new ArgumentException("Content block id must not be empty.", nameof(contentBlockId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.ContentBlockDeleted,
            actorUserProfileId,
            contentBlockResourceType,
            contentBlockId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records a scene deletion (<see cref="AuditAction.SceneDeleted"/>) — the audit fact written when an
    /// authorized host deletes a scene from a workspace, removing it (together with its child content blocks
    /// and their inline revision history) and its dependent visibility rules and asset links, after which the
    /// remaining scenes re-pack their ordering (CORE-LIFE-005). It is the scene counterpart of
    /// <see cref="ForEntityDeletion"/> and <see cref="ForContentBlockDeletion"/> and, like them, a thin
    /// specialization of <see cref="Create"/> that pins the action and applies the deletion producer's
    /// stricter contract: the tenant, the workspace the scene belonged to, the authenticated actor (the host
    /// who deleted the scene) and the deleted scene resource (its generic kind name and surrogate id) are all
    /// REQUIRED, where the generic factory leaves them optional. A deletion is a removal rather than a
    /// transition, and a scene has no lifecycle state, so there is NO before/after state pair (both null). The
    /// resource kind is passed as a generic NAME string (e.g. <c>Scene</c>) so the Audit module does not
    /// depend on the Scenes module's types, exactly like <see cref="ForVisibilityRuleChange"/> takes
    /// visibility state names as strings. Every value is an identifier or a generic name — never free-form
    /// content (threat T7) — and the audit row outlives the now-deleted scene it references because the
    /// reference is a recorded fact, not a foreign key (see the type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the deletion happened in (required).</param>
    /// <param name="workspaceId">The workspace the deleted scene belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the deletion (required; the audited actor).</param>
    /// <param name="sceneResourceType">The deleted scene's generic kind name (e.g. Scene).</param>
    /// <param name="sceneId">The deleted scene's surrogate id.</param>
    /// <param name="createdAt">When the deletion happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the scene resource type is blank.
    /// </exception>
    public static AuditLogEntry ForSceneDeletion(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string sceneResourceType,
        Guid sceneId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(sceneResourceType))
        {
            throw new ArgumentException("Scene resource type must not be empty.", nameof(sceneResourceType));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.SceneDeleted,
            actorUserProfileId,
            sceneResourceType,
            sceneId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records an asset deletion (<see cref="AuditAction.AssetDeleted"/>) — the audit fact written when an
    /// authorized host deletes an asset, removing its links and the underlying storage object (CORE-LIFE-006).
    /// It is the asset counterpart of <see cref="ForEntityDeletion"/>, <see cref="ForContentBlockDeletion"/>
    /// and <see cref="ForSceneDeletion"/> and, like them, a thin specialization of <see cref="Create"/> that
    /// pins the action and applies the deletion producer's stricter contract: the tenant, the workspace the
    /// asset belonged to, the authenticated actor (the host who deleted the asset) and the deleted asset
    /// resource (its generic kind name and surrogate id) are all REQUIRED, where the generic factory leaves
    /// them optional. A deletion is a removal rather than a transition, so there is NO before/after state pair
    /// (both null) — the asset's lifecycle status is irrelevant once it is gone. The resource kind is passed as
    /// a generic NAME string (e.g. <c>Asset</c>) so the Audit module does not depend on the Assets module's
    /// types, exactly like <see cref="ForVisibilityRuleChange"/> takes visibility state names as strings. Every
    /// value is an identifier or a generic name — never a storage coordinate or any free-form content (threats
    /// T4/T7) — and the audit row outlives the now-deleted asset it references because the reference is a
    /// recorded fact, not a foreign key (see the type summary).
    /// </summary>
    /// <param name="organizationId">The tenant the deletion happened in (required).</param>
    /// <param name="workspaceId">The workspace the deleted asset belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the deletion (required; the audited actor).</param>
    /// <param name="assetResourceType">The deleted asset's generic kind name (e.g. Asset).</param>
    /// <param name="assetId">The deleted asset's surrogate id.</param>
    /// <param name="createdAt">When the deletion happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty or the asset resource type is blank.
    /// </exception>
    public static AuditLogEntry ForAssetDeletion(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string assetResourceType,
        Guid assetId,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(assetResourceType))
        {
            throw new ArgumentException("Asset resource type must not be empty.", nameof(assetResourceType));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(assetId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.AssetDeleted,
            actorUserProfileId,
            assetResourceType,
            assetId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records a workspace archive (<see cref="AuditAction.WorkspaceArchived"/>) — the audit fact written when
    /// an authorized owner archives a workspace, taking it read-only and out of the active list (CORE-LIFE-009).
    /// A thin specialization of <see cref="Create"/> that pins the action and applies the archive producer's
    /// stricter contract: the tenant, the archived workspace, the authenticated actor (the owner who archived it)
    /// and the workspace resource (its generic kind name and surrogate id) are all REQUIRED, where the generic
    /// factory leaves them optional. Unlike the deletion factories, an archive is a real STATE TRANSITION — the
    /// workspace survives — so it records the before/after status NAMES (e.g. <c>Active</c> -&gt; <c>Archived</c>),
    /// exactly as <see cref="ForVisibilityRuleChange"/> records a visibility transition. The archived workspace is
    /// both the scope (<paramref name="workspaceId"/>) and the governed resource (its id), because the action is
    /// performed ON the workspace. The resource kind and the state names are passed as generic strings so the
    /// Audit module does not depend on the Workspaces module's types. Every value is an identifier, an enum or a
    /// generic state name — never free-form content (threat T7) — and the audit row outlives any later change to
    /// the workspace it references because the reference is a recorded fact, not a foreign key (see the type
    /// summary).
    /// </summary>
    /// <param name="organizationId">The tenant the archive happened in (required).</param>
    /// <param name="workspaceId">The workspace that was archived (required for this action).</param>
    /// <param name="actorUserProfileId">The owner who performed the archive (required; the audited actor).</param>
    /// <param name="workspaceResourceType">The archived workspace's generic kind name (e.g. Workspace).</param>
    /// <param name="previousState">The lifecycle status name before the archive (e.g. Active; required).</param>
    /// <param name="newState">The lifecycle status name after the archive (e.g. Archived; required).</param>
    /// <param name="createdAt">When the archive happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the workspace resource type / a state name is blank.
    /// </exception>
    public static AuditLogEntry ForWorkspaceArchive(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string workspaceResourceType,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(workspaceResourceType))
        {
            throw new ArgumentException("Workspace resource type must not be empty.", nameof(workspaceResourceType));
        }

        // An archive is a real state transition, so both before and after status names are required even though
        // the generic factory leaves the state pair optional.
        if (string.IsNullOrWhiteSpace(previousState))
        {
            throw new ArgumentException("Previous state must not be empty.", nameof(previousState));
        }

        if (string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be empty.", nameof(newState));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.WorkspaceArchived,
            actorUserProfileId,
            workspaceResourceType,
            workspaceId,
            targetParticipantId: null,
            previousState: previousState,
            newState: newState,
            createdAt);
    }

    /// <summary>
    /// Records a session cancellation (<see cref="AuditAction.SessionCancelled"/>) — the audit fact written when
    /// an authorized host cancels a not-yet-started session, taking it out of use before it ever runs
    /// (CORE-LIFE-010). It is the session counterpart of <see cref="ForWorkspaceArchive"/> and, like it, a thin
    /// specialization of <see cref="Create"/> that pins the action and applies the cancel producer's stricter
    /// contract: the tenant, the workspace the session belonged to, the authenticated actor (the host who
    /// cancelled the session) and the cancelled session resource (its generic kind name and surrogate id) are all
    /// REQUIRED, where the generic factory leaves them optional. Unlike the deletion factories, a cancel is a real
    /// STATE TRANSITION — the session row survives so its append-only <c>session_events</c> and audit history is
    /// preserved (the story note: "NEVER delete append-only session_events or audit_logs - prefer a Cancelled
    /// status") — so it records the before/after status NAMES (e.g. <c>Prepared</c> -&gt; <c>Cancelled</c>),
    /// exactly as <see cref="ForWorkspaceArchive"/> records the archive transition. The session is both the scope
    /// (<paramref name="workspaceId"/> is the session's workspace) and the governed resource (its id), because the
    /// action is performed ON the session. The resource kind and the state names are passed as generic strings so
    /// the Audit module does not depend on the Sessions module's types. Every value is an identifier, an enum or a
    /// generic state name — never free-form content (threat T7) — and the audit row outlives any later change to
    /// the session it references because the reference is a recorded fact, not a foreign key (see the type
    /// summary).
    /// </summary>
    /// <param name="organizationId">The tenant the cancellation happened in (required).</param>
    /// <param name="workspaceId">The workspace the cancelled session belonged to (required for this action).</param>
    /// <param name="actorUserProfileId">The host who performed the cancellation (required; the audited actor).</param>
    /// <param name="sessionResourceType">The cancelled session's generic kind name (e.g. Session).</param>
    /// <param name="sessionId">The cancelled session's surrogate id.</param>
    /// <param name="previousState">The lifecycle status name before the cancel (e.g. Prepared; required).</param>
    /// <param name="newState">The lifecycle status name after the cancel (e.g. Cancelled; required).</param>
    /// <param name="createdAt">When the cancellation happened.</param>
    /// <exception cref="ArgumentException">
    /// A required id is empty, or the session resource type / a state name is blank.
    /// </exception>
    public static AuditLogEntry ForSessionCancellation(
        Guid organizationId,
        Guid workspaceId,
        Guid actorUserProfileId,
        string sessionResourceType,
        Guid sessionId,
        string previousState,
        string newState,
        DateTimeOffset createdAt)
        => ForSessionLifecycleTransition(
            AuditAction.SessionCancelled,
            organizationId,
            workspaceId,
            actorUserProfileId,
            sessionResourceType,
            sessionId,
            previousState,
            newState,
            createdAt);

    // --- Entitlement / store / purchase audit facts (CORE-SPEC-002) -------------
    //
    // These back the entitlement/store domain events csv/entitlement_event_catalog.csv marks audit=true. The
    // grant/revoke, purchase-verification and store-notification facts are PLATFORM-LEVEL (a null organization):
    // a purchase and the entitlement it grants are deployment-spanning, not tenant-scoped (docs/21). QuotaExceeded
    // is the exception — a quota is denied inside an already tenant-scoped command, so it is a normal tenant fact.

    /// <summary>
    /// Records a quota denial (<see cref="AuditAction.QuotaExceeded"/>) — the audit fact written when a protected,
    /// server-enforced command is REFUSED because it would exceed the subject's quota limit (CORE-SPEC-002; emitted
    /// by the <see cref="LiveCore.Api.Entitlements.QuotaEnforcementService"/> callers). It is a TENANT-scoped fact:
    /// the denial happens inside an already authenticated, tenant-scoped command, so the tenant, the denied caller
    /// (the actor) and the quota subject (the generic subject-kind name and id, as the governed resource) are all
    /// REQUIRED; the workspace is set where the command has one and null otherwise (a user-subject quota such as
    /// <c>workspace.active.max</c> has none). It records no caller-supplied content (threat T7).
    /// </summary>
    /// <param name="organizationId">The tenant the denied command ran in (required).</param>
    /// <param name="workspaceId">The workspace the command concerned, or <see langword="null"/> when it has none.</param>
    /// <param name="actorUserProfileId">The caller whose command was denied (required; the audited actor).</param>
    /// <param name="quotaSubjectType">The generic kind name of the quota subject (e.g. User/Workspace/Session).</param>
    /// <param name="quotaSubjectId">The quota subject's id.</param>
    /// <param name="createdAt">When the denial happened.</param>
    /// <exception cref="ArgumentException">A required id is empty or the subject type is blank.</exception>
    public static AuditLogEntry ForQuotaExceeded(
        Guid organizationId,
        Guid? workspaceId,
        Guid actorUserProfileId,
        string quotaSubjectType,
        Guid quotaSubjectId,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (string.IsNullOrWhiteSpace(quotaSubjectType))
        {
            throw new ArgumentException("Quota subject type must not be empty.", nameof(quotaSubjectType));
        }

        if (quotaSubjectId == Guid.Empty)
        {
            throw new ArgumentException("Quota subject id must not be empty.", nameof(quotaSubjectId));
        }

        return Create(
            organizationId,
            workspaceId,
            AuditAction.QuotaExceeded,
            actorUserProfileId,
            quotaSubjectType,
            quotaSubjectId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records an entitlement grant (<see cref="AuditAction.EntitlementGranted"/>) — the PLATFORM-level audit fact
    /// written when a verified, buyer-linked purchase grants the buyer the mapped entitlement (CORE-SPEC-002, the
    /// CORE-MON-003 grant chain). The grant is deployment-spanning (a subject is not tenant-scoped), so the entry
    /// carries a NULL organization and, being system-initiated by a verified purchase, no actor; the granted
    /// subject is the governed resource (its generic kind name and id). It records no receipt, proof or content
    /// (threat T7).
    /// </summary>
    /// <param name="subjectType">The generic kind name of the granted subject (e.g. User).</param>
    /// <param name="subjectId">The granted subject's id.</param>
    /// <param name="createdAt">When the grant happened.</param>
    /// <exception cref="ArgumentException">The subject id is empty or the subject type is blank.</exception>
    public static AuditLogEntry ForEntitlementGranted(string subjectType, Guid subjectId, DateTimeOffset createdAt)
        => ForEntitlementChange(AuditAction.EntitlementGranted, subjectType, subjectId, createdAt);

    /// <summary>
    /// Records an entitlement revocation (<see cref="AuditAction.EntitlementRevoked"/>) — the PLATFORM-level audit
    /// fact written when a refund, cancellation or chargeback revokes the entitlement a purchase had granted
    /// (CORE-SPEC-002, the CORE-MON-004 revocation, the inverse of <see cref="ForEntitlementGranted"/>). Same shape
    /// as the grant fact: a null organization and no actor (system-initiated), with the affected subject as the
    /// governed resource.
    /// </summary>
    /// <param name="subjectType">The generic kind name of the affected subject (e.g. User).</param>
    /// <param name="subjectId">The affected subject's id.</param>
    /// <param name="createdAt">When the revocation happened.</param>
    /// <exception cref="ArgumentException">The subject id is empty or the subject type is blank.</exception>
    public static AuditLogEntry ForEntitlementRevoked(string subjectType, Guid subjectId, DateTimeOffset createdAt)
        => ForEntitlementChange(AuditAction.EntitlementRevoked, subjectType, subjectId, createdAt);

    /// <summary>
    /// Shared specialization behind <see cref="ForEntitlementGranted"/> and <see cref="ForEntitlementRevoked"/>: a
    /// platform-level (null organization), system-initiated (null actor) audit fact recording an entitlement change
    /// on a subject (the governed resource). The two differ only in the action they pin.
    /// </summary>
    private static AuditLogEntry ForEntitlementChange(
        AuditAction action,
        string subjectType,
        Guid subjectId,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(subjectType))
        {
            throw new ArgumentException("Subject type must not be empty.", nameof(subjectType));
        }

        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        return Create(
            organizationId: null,
            workspaceId: null,
            action,
            actorUserProfileId: null,
            subjectType,
            subjectId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records a purchase-verification audit fact (<see cref="AuditAction.PurchaseVerificationSubmitted"/>,
    /// <see cref="AuditAction.PurchaseVerificationSucceeded"/> or <see cref="AuditAction.PurchaseVerificationFailed"/>)
    /// — the PLATFORM-level fact written by the Apple/Google verification endpoints as a buyer submits a purchase
    /// proof, it is verified and recorded, or it is rejected (CORE-SPEC-002). The buyer is the actor; the purchase
    /// is deployment-spanning, so the entry carries a NULL organization. On success the recorded purchase
    /// transaction is the governed resource (so the audit names WHICH purchase was verified); on submission/failure
    /// there is no recorded purchase yet, so the resource is omitted. It records no proof, receipt content or
    /// rejection detail (threat T7).
    /// </summary>
    /// <param name="action">The verification action (Submitted / Succeeded / Failed).</param>
    /// <param name="actorUserProfileId">The authenticated buyer (required; the audited actor).</param>
    /// <param name="purchaseTransactionId">The recorded purchase transaction id on success, or <see langword="null"/> on submission/failure.</param>
    /// <param name="createdAt">When the verification step happened.</param>
    /// <exception cref="ArgumentException">The actor id is empty, or the purchase transaction id is explicitly empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The action is not a purchase-verification action.</exception>
    public static AuditLogEntry ForPurchaseVerification(
        AuditAction action,
        Guid actorUserProfileId,
        Guid? purchaseTransactionId,
        DateTimeOffset createdAt)
    {
        if (action is not (AuditAction.PurchaseVerificationSubmitted
            or AuditAction.PurchaseVerificationSucceeded
            or AuditAction.PurchaseVerificationFailed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action), action, "Action is not a purchase-verification action.");
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // The recorded purchase is the governed resource only when there is one (success); the (type, id) pair is
        // supplied together or both omitted.
        var resourceType = purchaseTransactionId is null ? null : "PurchaseTransaction";

        return Create(
            organizationId: null,
            workspaceId: null,
            action,
            actorUserProfileId,
            resourceType,
            purchaseTransactionId,
            targetParticipantId: null,
            previousState: null,
            newState: null,
            createdAt);
    }

    /// <summary>
    /// Records the receipt of a store notification (<see cref="AuditAction.StoreNotificationReceived"/>) — the
    /// PLATFORM-level, system audit fact written by <see cref="LiveCore.Api.Store.StoreNotificationService"/> on the
    /// first arrival of a validated, normalized notification, before its effect is applied (CORE-SPEC-002). The
    /// notification is deployment-spanning, so the entry carries a NULL organization and no actor (system). The
    /// generic notification-type name is recorded as the descriptive value so the audit names WHAT kind of
    /// notification arrived; no payload or receipt content is ever recorded (threat T7).
    /// </summary>
    /// <param name="notificationType">The generic notification-type name (e.g. Renewed/Refunded/Cancelled).</param>
    /// <param name="createdAt">When the notification was received.</param>
    /// <exception cref="ArgumentException">The notification type is blank.</exception>
    public static AuditLogEntry ForStoreNotificationReceived(string notificationType, DateTimeOffset createdAt)
        => ForStoreNotification(AuditAction.StoreNotificationReceived, notificationType, createdAt);

    /// <summary>
    /// Records that a store notification was idempotently applied (<see cref="AuditAction.StoreNotificationProcessed"/>)
    /// — the PLATFORM-level, system audit fact written by <see cref="LiveCore.Api.Store.StoreNotificationService"/>
    /// once a notification's effect is committed (CORE-SPEC-002, CORE-MON-010). Like the receipt fact it carries a
    /// NULL organization and no actor; the generic applied-outcome name is recorded as the descriptive value (e.g.
    /// Applied/Unchanged), and no payload or receipt content is recorded (threat T7).
    /// </summary>
    /// <param name="outcome">The generic applied-outcome name (e.g. Applied/Unchanged).</param>
    /// <param name="createdAt">When the notification was processed.</param>
    /// <exception cref="ArgumentException">The outcome is blank.</exception>
    public static AuditLogEntry ForStoreNotificationProcessed(string outcome, DateTimeOffset createdAt)
        => ForStoreNotification(AuditAction.StoreNotificationProcessed, outcome, createdAt);

    /// <summary>
    /// Shared specialization behind <see cref="ForStoreNotificationReceived"/> and
    /// <see cref="ForStoreNotificationProcessed"/>: a platform-level (null organization), system (null actor) audit
    /// fact whose generic descriptive name (the notification type, or the applied outcome) is recorded in the
    /// new-state field — the only descriptive slot a tenant-less notification, named by a non-GUID provider key,
    /// can occupy without storing any payload (threat T7).
    /// </summary>
    private static AuditLogEntry ForStoreNotification(AuditAction action, string descriptor, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            throw new ArgumentException("Store notification descriptor must not be empty.", nameof(descriptor));
        }

        return Create(
            organizationId: null,
            workspaceId: null,
            action,
            actorUserProfileId: null,
            resourceType: null,
            resourceId: null,
            targetParticipantId: null,
            previousState: null,
            newState: descriptor,
            createdAt);
    }

    /// <summary>
    /// SEALS the entry into its tenant's tamper-evident hash chain at append time (CORE-SEC-003): stamps the
    /// per-tenant append <see cref="Sequence"/> the allocator handed out, records the
    /// <paramref name="previousHash"/> link to the preceding entry (<see langword="null"/> for the genesis
    /// entry) and computes this entry's <see cref="EntryHash"/> over its recorded fields and that link. Called
    /// ONCE by <see cref="AuditLogRepository.AppendAsync"/> immediately before the insert; the entry stays
    /// otherwise immutable (every recorded fact was fixed at construction), so sealing only adds the chain
    /// linkage and never alters an audited value. It is idempotent-guarded: a second call throws, because an
    /// already-chained entry must never be re-linked.
    /// </summary>
    /// <param name="sequence">The per-tenant append sequence number (strictly positive).</param>
    /// <param name="previousHash">The preceding entry's hash, or <see langword="null"/> for the genesis entry.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sequence is not strictly positive.</exception>
    /// <exception cref="ArgumentException">The previous hash is not a well-formed chain hash.</exception>
    /// <exception cref="InvalidOperationException">The entry has already been sealed.</exception>
    internal void Seal(long sequence, string? previousHash)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "The append sequence must be strictly positive.");
        }

        if (previousHash is not null && previousHash.Length != AuditLogChain.HashHexLength)
        {
            throw new ArgumentException("The previous hash is not a well-formed chain hash.", nameof(previousHash));
        }

        if (EntryHash is not null)
        {
            throw new InvalidOperationException("The audit log entry has already been sealed into the chain.");
        }

        Sequence = sequence;
        PreviousHash = previousHash;
        EntryHash = AuditLogChain.ComputeEntryHash(this, previousHash);
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: the row id, tenant, workspace,
    /// action, actor, governed resource, target and the before/after state names. Every field is an
    /// identifier, an enum or a generic state name, never free-form content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"AuditLogEntry {Id} action={Action} org={(OrganizationId is { } org ? org.ToString() : "platform")} "
            + $"ws={(WorkspaceId is { } ws ? ws.ToString() : "none")} "
            + $"actor={(ActorUserProfileId is { } actor ? actor.ToString() : "system")} "
            + $"resource={ResourceType ?? "none"}:{(ResourceId is { } rid ? rid.ToString() : "none")} "
            + $"target={(TargetParticipantId is { } target ? target.ToString() : "audience")} "
            + $"state={PreviousState ?? "none"}->{NewState ?? "none"}";
}
