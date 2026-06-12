namespace LiveCore.Api.Audit;

/// <summary>
/// An append-only audit log entry (CORE-VIS-006, the LAST story of the "Visibility and Reveal Engine"
/// epic). The Audit module — FIRST appearing here — owns the "append-only audit log" and the "security
/// event records" (docs/05_MODULE_CONTRACTS.md; csv/database_tables.csv: table <c>audit_logs</c>,
/// module Audit, scope <c>organization</c>, "Append-only audit"). An <see cref="AuditLogEntry"/> is the
/// durable, immutable record of one security-relevant action — for this story, a visibility rule change
/// (<see cref="AuditAction.VisibilityRuleChanged"/>), the docs/07_SECURITY_THREAT_MODEL.md required
/// control "audit creation for visibility changes".
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
/// also workspace-scoped, so this story always sets <see cref="WorkspaceId"/>; the column is nullable
/// only so later org-level audit events (CORE-AUD-001) need no schema change.
///
/// NO SENSITIVE CONTENT (threat T7). Every field is an identifier, an enum or a generic state NAME
/// (Hidden/Visible) — never free-form scene/content body — so <see cref="ToString"/> is safe for
/// structured logs, and the audit log itself never stores the revealed content, only the fact that a
/// visibility change happened, to whom and from which state to which.
///
/// GENERIC, EXTENSIBLE SHAPE. The columns are generic on purpose (a generic action, an optional
/// resource reference, an optional before/after state pair) so the generic append-only audit log story
/// (CORE-AUD-001) and the audit query permissions story (CORE-AUD-005) extend this table by adding
/// actions and read paths, not by reshaping it. This story exposes exactly ONE factory —
/// <see cref="ForVisibilityRuleChange"/> — because the visibility reveal command is its only producer
/// today; no HTTP read route is built here (viewing the audit log is CORE-AUD-005, the "View audit
/// log" row of docs/06_AUTHORIZATION_MATRIX.md).
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
        string newState,
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

        if (targetParticipantId == Guid.Empty)
        {
            throw new ArgumentException(
                "Target participant id must not be empty; pass null for an audience-wide entry.",
                nameof(targetParticipantId));
        }

        if (string.IsNullOrWhiteSpace(newState))
        {
            throw new ArgumentException("New state must not be empty.", nameof(newState));
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
        NewState = null!;
    }

    /// <summary>Surrogate key of the row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).</summary>
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

    /// <summary>The generic state AFTER the action (the new visibility name, e.g. Visible). Required.</summary>
    public string NewState { get; }

    /// <summary>When the audited action happened (UTC). Part of the critical index with the tenant.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Records a visibility rule change (<see cref="AuditAction.VisibilityRuleChanged"/>) — the audit
    /// fact written by the Visibility module's reveal command when a resource's audience visibility
    /// actually changes (CORE-VIS-006). The caller (the reveal command, the only producer) supplies the
    /// already-resolved tenant, workspace, the authenticated actor, the governed resource, the optional
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

        return new AuditLogEntry(
            Guid.CreateVersion7(),
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
            + $"state={PreviousState ?? "none"}->{NewState}";
}
