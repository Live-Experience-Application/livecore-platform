namespace LiveCore.Api.Audit;

/// <summary>
/// The kind of security-relevant action recorded by an <see cref="AuditLogEntry"/> in the append-only
/// audit log. The Audit module owns the "security event records" (docs/05_MODULE_CONTRACTS.md), and this
/// enum is the Core-level catalog of the generic actions those records capture. The names are the
/// generic, product-neutral event names from docs/09_EVENT_CATALOG.md — never vertical terms.
///
/// CORE-VIS-006 seeded this catalog with the one action that epic produced,
/// <see cref="VisibilityRuleChanged"/> (the security-relevant visibility change of
/// docs/09_EVENT_CATALOG.md). CORE-AUD-001 — the generic append-only audit log — promotes the catalog to
/// a GENERIC one: the actions below name the product-neutral, security-relevant Core actions the log can
/// record through the generic <see cref="AuditLogEntry.Create"/> factory, not just visibility changes.
/// Each producer command wires its own action in its own story (exactly as the durable realtime events
/// are wired per command); cataloguing an action here is the generic audit contract, not a second
/// implementation of the command.
///
/// EXTENSIBLE WITHOUT A SCHEMA CHANGE: the action is persisted as a real string column by its stable
/// NAME, so adding a member never reshapes the table. The integer values are only in-memory storage
/// discriminators (persisted by name, like every other enum in the model — <c>VisibilityState</c>,
/// <c>SessionStatus</c>, <c>ContentBlockType</c>), carry no ordering meaning and must not be compared
/// with &gt;/&lt;.
/// </summary>
public enum AuditAction
{
    /// <summary>
    /// A resource's audience visibility rule changed — a host revealed a resource to the audience or to
    /// a selected participant (the Visibility module's reveal command, CORE-VIS-004/005). This is the
    /// security-relevant <c>VisibilityRuleChanged</c> event of docs/09_EVENT_CATALOG.md, recorded here
    /// as an append-only audit fact so every visibility change is auditable (the primary security
    /// promise of docs/07_SECURITY_THREAT_MODEL.md: "audit creation for visibility changes"). It is
    /// distinct from the durable realtime SESSION event of the same name that the Realtime epic
    /// (CORE-RT-003) will append to the <c>session_events</c> stream — that is event DELIVERY; this is
    /// the security AUDIT record.
    /// </summary>
    VisibilityRuleChanged = 1,

    /// <summary>
    /// A session moved from <c>Prepared</c> to <c>Live</c> — the Sessions module's start command
    /// (CORE-SES-004). The security-relevant <c>SessionStarted</c> event of docs/09_EVENT_CATALOG.md
    /// ("starts live timeline"), recordable as a generic audit fact: a workspace-scoped action with no
    /// governed resource and no before/after visibility state (the session status transition is the
    /// session's own authoritative state, not an audit state pair).
    /// </summary>
    SessionStarted = 2,

    /// <summary>
    /// A session moved from <c>Live</c> to <c>Ended</c> — the Sessions module's end command
    /// (CORE-SES-004). The security-relevant <c>SessionEnded</c> event of docs/09_EVENT_CATALOG.md
    /// ("ends live timeline"), recordable as a generic, workspace-scoped audit fact with no governed
    /// resource and no state pair.
    /// </summary>
    SessionEnded = 3,

    /// <summary>
    /// A workspace member invitation was created — the Workspaces module's invite command. Auditing the
    /// invite is the threat model's stated control against invite abuse (threat T6 in
    /// docs/07_SECURITY_THREAT_MODEL.md lists "audit logs" among the controls). A generic,
    /// workspace-scoped audit fact: the actor is the host who issued the invite and there is no
    /// before/after visibility state. The invite TOKEN itself is never recorded (threats T6/T7) — only
    /// the fact that an invite was created.
    /// </summary>
    MemberInvited = 4,

    /// <summary>
    /// A workspace or organization member was removed, revoking the subject's access — the member removal
    /// command (CORE-LIFE-001). Auditing the removal is the threat model's stated control for access
    /// revocation: it records that an authorized admin took a member's standing away (threats T1/T6 in
    /// docs/07_SECURITY_THREAT_MODEL.md). A generic audit fact whose actor is the admin who performed the
    /// removal, whose resource is the removed membership (its generic kind name and surrogate id), and
    /// whose previous state records the role that was revoked; it is workspace-scoped for a workspace
    /// member removal and organization-level (no workspace) for an organization member removal. The
    /// removal is a deletion, not a transition to a new state, so the new state is null
    /// (<see cref="AuditLogEntry.ForMemberRemoval"/>).
    /// </summary>
    MemberRemoved = 5,

    /// <summary>
    /// A host deleted an entity, removing it and its dependent edges, visibility rules and asset links —
    /// the entity deletion command (CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic). Auditing
    /// the deletion satisfies the story's "deletion is authorized and audited" criterion: it records that
    /// an authorized host removed a workspace resource (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
    /// A generic, workspace-scoped audit fact whose actor is the host who deleted the entity and whose
    /// resource is the deleted entity (its generic kind name and surrogate id). The deletion is a removal,
    /// not a transition to a new state, so there is no before/after state pair (the entity has no lifecycle
    /// state); the dependent rows the deletion cascades are consequences of the same action and are not
    /// separately audited, exactly as the member-removal action records one fact for the removal it
    /// performs (<see cref="AuditLogEntry.ForEntityDeletion"/>).
    /// </summary>
    EntityDeleted = 6,

    /// <summary>
    /// A host deleted a content block from a scene, removing it (together with its revision history, which
    /// lives inline on the row) and its dependent visibility rules and asset links — the content block
    /// deletion command (CORE-LIFE-004, the "Resource Lifecycle and Deletion" epic). Recording the deletion
    /// keeps a content-block removal auditable exactly as an entity removal is, the consistent application of
    /// docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion implementations follow
    /// this decision: cascade the dependents, in the application, inside one transaction, and audit the
    /// deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the content
    /// block and whose resource is the deleted content block (its generic kind name and surrogate id). The
    /// deletion is a removal, not a transition to a new state, so there is no before/after state pair (a
    /// content block has no lifecycle state); the dependent rows the deletion cascades are consequences of
    /// the same action and are not separately audited, exactly as <see cref="EntityDeleted"/> records one
    /// fact for the deletion it performs (<see cref="AuditLogEntry.ForContentBlockDeletion"/>).
    /// </summary>
    ContentBlockDeleted = 7,
}
