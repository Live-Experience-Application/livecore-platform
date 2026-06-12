namespace LiveCore.Api.Audit;

/// <summary>
/// The kind of security-relevant action recorded by an <see cref="AuditLogEntry"/> in the append-only
/// audit log (CORE-VIS-006). The Audit module owns the "security event records" (docs/05_MODULE_CONTRACTS.md),
/// and this enum is the Core-level catalog of the actions those records capture. The names are the
/// generic, product-neutral event names from docs/09_EVENT_CATALOG.md — never vertical terms.
///
/// THIS STORY adds exactly one action: <see cref="VisibilityRuleChanged"/>, the security-relevant
/// visibility change of docs/09_EVENT_CATALOG.md ("VisibilityRuleChanged | Host/CoHost |
/// Host/CoHost/Audit | yes | security-relevant"). The enum is deliberately small and EXTENSIBLE: later
/// Audit stories (the generic append-only audit log, CORE-AUD-001, and onward) add further members
/// WITHOUT a schema change, because the action is persisted as a real string column. The integer
/// values are only in-memory storage discriminators (persisted by their stable NAME, like every other
/// enum in the model — <c>VisibilityState</c>, <c>SessionStatus</c>, <c>ContentBlockType</c>), carry
/// no ordering meaning and must not be compared with &gt;/&lt;.
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
}
