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
}
