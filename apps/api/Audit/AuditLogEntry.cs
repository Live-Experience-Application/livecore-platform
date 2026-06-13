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
/// TENANT SCOPE. The audit log is tenant-scoped (csv/database_tables.csv scope <c>organization</c>), so
/// every entry carries <see cref="OrganizationId"/>; the documented critical index is
/// <c>audit_logs(organization_id, created_at)</c> (docs/10_DATABASE_SCHEMA.md). A visibility change is
/// also workspace-scoped, so the visibility producer always sets <see cref="WorkspaceId"/>; the column
/// is nullable so an organization-level generic action (CORE-AUD-001) needs no schema change.
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
        Guid organizationId,
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

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
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
    /// <c>organization_id</c> foreign key to <c>organizations</c>). Part of the documented critical
    /// index <c>audit_logs(organization_id, created_at)</c>; the audit log is read tenant-scoped so one
    /// tenant's records are never returned through another tenant's id (threat T5).
    /// </summary>
    public Guid OrganizationId { get; }

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
    /// <param name="organizationId">The tenant the action happened in (required).</param>
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
    /// The organization id is empty, a SET optional id is explicitly empty, the resource reference is
    /// half-set, or a SET resource type / state value is blank.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The action is not a defined <see cref="AuditAction"/>.</exception>
    public static AuditLogEntry Create(
        Guid organizationId,
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
    /// Identifier-only representation that is safe for structured logs: the row id, tenant, workspace,
    /// action, actor, governed resource, target and the before/after state names. Every field is an
    /// identifier, an enum or a generic state name, never free-form content (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"AuditLogEntry {Id} action={Action} org={OrganizationId} "
            + $"ws={(WorkspaceId is { } ws ? ws.ToString() : "none")} "
            + $"actor={(ActorUserProfileId is { } actor ? actor.ToString() : "system")} "
            + $"resource={ResourceType ?? "none"}:{(ResourceId is { } rid ? rid.ToString() : "none")} "
            + $"target={(TargetParticipantId is { } target ? target.ToString() : "audience")} "
            + $"state={PreviousState ?? "none"}->{NewState ?? "none"}";
}
