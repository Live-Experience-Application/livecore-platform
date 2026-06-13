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
    /// A session moved from <c>Prepared</c> to <c>Live</c> — the Sessions module's start command, wired as
    /// a producer by CORE-EVT-001. The security-relevant <c>SessionStarted</c> event of
    /// docs/09_EVENT_CATALOG.md ("starts live timeline"). Like <see cref="SessionCancelled"/> it records a
    /// real STATE TRANSITION (the session survives), so the entry carries the before/after status NAMES
    /// (<c>Prepared</c> -&gt; <c>Live</c>), the session as the governed resource and the host who started
    /// it as the actor — a generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForSessionStart"/>).
    /// </summary>
    SessionStarted = 2,

    /// <summary>
    /// A session moved from <c>Live</c> to <c>Ended</c> — the Sessions module's end command, wired as a
    /// producer by CORE-EVT-001. The security-relevant <c>SessionEnded</c> event of
    /// docs/09_EVENT_CATALOG.md ("ends live timeline"). Like <see cref="SessionStarted"/> it records the
    /// before/after status NAMES (<c>Live</c> -&gt; <c>Ended</c>), the session as the governed resource and
    /// the host who ended it as the actor — a generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForSessionEnd"/>).
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

    /// <summary>
    /// A host deleted a scene from a workspace, removing it together with its child content blocks (and
    /// their inline revision history) and every dependent visibility rule and asset link — the scene
    /// deletion command (CORE-LIFE-005, the "Resource Lifecycle and Deletion" epic). After the deletion the
    /// remaining scenes re-pack their ordering so there is no gap; the re-pack is part of the same action and
    /// is not separately audited. Recording the deletion keeps a scene removal auditable exactly as an entity
    /// or content-block removal is, the consistent application of
    /// docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion implementations follow
    /// this decision: cascade the dependents, in the application, inside one transaction, and audit the
    /// deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the scene and
    /// whose resource is the deleted scene (its generic kind name and surrogate id). The deletion is a
    /// removal, not a transition to a new state, so there is no before/after state pair (a scene has no
    /// lifecycle state); the dependent rows the deletion cascades and the order re-pack are consequences of
    /// the same action and are not separately audited, exactly as <see cref="EntityDeleted"/> and
    /// <see cref="ContentBlockDeleted"/> record one fact for the deletion they perform
    /// (<see cref="AuditLogEntry.ForSceneDeletion"/>).
    /// </summary>
    SceneDeleted = 8,

    /// <summary>
    /// A host deleted an asset, removing its links and the underlying storage object — the host-initiated
    /// asset deletion command (CORE-LIFE-006, the "Resource Lifecycle and Deletion" epic). Until now an
    /// available asset could be created and linked but never removed; this adds the inverse. Recording the
    /// deletion keeps an asset removal auditable exactly as an entity, content-block or scene removal is, the
    /// consistent application of docs/adr/0012-resource-deletion-cascades-dependents.md ("All resource-deletion
    /// implementations follow this decision: cascade the dependents, in the application, inside one transaction,
    /// and audit the deletion"). A generic, workspace-scoped audit fact whose actor is the host who deleted the
    /// asset and whose resource is the deleted asset (its generic kind name and surrogate id). The deletion is a
    /// removal, not a transition to a new state, so there is no before/after state pair (the asset's lifecycle
    /// status is irrelevant once it is gone); the asset's links the deletion cascades and the storage object the
    /// deletion removes are consequences of the same action and are not separately audited, exactly as
    /// <see cref="EntityDeleted"/>, <see cref="ContentBlockDeleted"/> and <see cref="SceneDeleted"/> record one
    /// fact for the deletion they perform (<see cref="AuditLogEntry.ForAssetDeletion"/>). The storage object key
    /// is never recorded — only identifiers, never a means to reach private content (threats T4/T7).
    /// </summary>
    AssetDeleted = 9,

    /// <summary>
    /// An owner archived a workspace, taking it out of active use — the workspace archive command
    /// (CORE-LIFE-009, the "Resource Lifecycle and Deletion" epic). Until now a workspace had create/read/update
    /// but no lifecycle end-state; this records the soft, terminal Active -&gt; Archived transition. Auditing the
    /// archive satisfies the story's "audited" criterion: it records that an authorized owner took a workspace
    /// read-only (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). Unlike the deletion actions, an archive is
    /// a real STATE TRANSITION (the workspace survives), so it records the before/after status NAMES
    /// (<c>Active</c> -&gt; <c>Archived</c>) exactly like <see cref="VisibilityRuleChanged"/> records a
    /// visibility transition; its actor is the owner who archived the workspace and its resource is the workspace
    /// itself (its generic kind name and surrogate id). A generic, workspace-scoped audit fact
    /// (<see cref="AuditLogEntry.ForWorkspaceArchive"/>).
    /// </summary>
    WorkspaceArchived = 10,

    /// <summary>
    /// A host cancelled a not-yet-started session, taking it out of use before it ever ran — the session
    /// cancel command (CORE-LIFE-010, the "Resource Lifecycle and Deletion" epic). Until now a session could
    /// be created, started and ended but never cancelled; this records the soft, terminal Prepared -&gt;
    /// Cancelled transition. Auditing the cancel satisfies the story's "authorized" criterion: it records that
    /// an authorized host took a session out of use (threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
    /// Unlike the deletion actions — and exactly like <see cref="WorkspaceArchived"/> — a cancel is a real
    /// STATE TRANSITION (the session row survives so its append-only <c>session_events</c> and audit history is
    /// preserved), so it records the before/after status NAMES (<c>Prepared</c> -&gt; <c>Cancelled</c>) like
    /// <see cref="VisibilityRuleChanged"/> records a visibility transition; its actor is the host who cancelled
    /// the session and its resource is the session itself (its generic kind name and surrogate id). A generic,
    /// workspace-scoped audit fact (<see cref="AuditLogEntry.ForSessionCancellation"/>).
    /// </summary>
    SessionCancelled = 11,
}
