namespace LiveCore.Api.Entitlements;

/// <summary>
/// The generic quota entitlement KEYS that protected Core commands enforce server-side (CORE-ENTL-004, the quota
/// enforcement story of the "Entitlements and Quotas" epic). Each constant is the canonical, lower-case dotted
/// entitlement key from the generic key list in docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md — product-neutral
/// Core vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv); a vertical maps each key to its own paywall copy
/// in its UI and never sees these names.
///
/// These are the keys a protected command names when it asks <see cref="QuotaEnforcementService"/> whether the
/// command may proceed. The key alone does not impose a limit: the deployment decides the per-subject limit by
/// defining a matching <see cref="QuotaDefinition"/> and granting a <see cref="SubjectEntitlement"/>. Naming the key
/// here only tells Core WHICH quota gates WHICH command, so the enforcement point and the
/// <c>GET /api/v1/.../quota-status</c> read both speak about the same quota.
/// </summary>
public static class QuotaEntitlementKeys
{
    /// <summary>
    /// Caps a user's active workspace count (docs/21 generic key <c>workspace.active.max</c>). Enforced on
    /// <c>POST /api/v1/workspaces</c> for the <see cref="EntitlementSubjectType.User"/> subject that creates the
    /// workspace, so a free user cannot create more workspaces than their plan allows
    /// (csv/mobile_entitlement_catalog.csv scope <c>user</c>).
    /// </summary>
    public const string WorkspaceActiveMax = "workspace.active.max";

    /// <summary>
    /// Caps a workspace's active (live) session count (docs/21 generic key <c>session.active.max</c>). Enforced on
    /// <c>POST /api/v1/sessions/{sessionId}/start</c> for the <see cref="EntitlementSubjectType.Workspace"/> subject
    /// that owns the session, and released on <c>.../end</c>, so a free workspace cannot run more concurrent live
    /// sessions than its plan allows (the workspace is the quota subject the workspace quota-status route already
    /// surfaces, CORE-ENTL-003).
    /// </summary>
    public const string SessionActiveMax = "session.active.max";
}
