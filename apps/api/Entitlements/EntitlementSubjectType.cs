namespace LiveCore.Api.Entitlements;

/// <summary>
/// The generic SUBJECT KIND an entitlement can be assigned to (CORE-ENTL-002, the subject entitlement
/// assignment and lookup story of the "Entitlements and Quotas" epic). A <see cref="SubjectEntitlement"/>
/// records that ONE generic subject holds a granted <see cref="EntitlementDefinition"/> at a concrete value;
/// this enum says WHAT kind of thing that subject is, so the per-subject premium state is keyed
/// unambiguously by the pair (<see cref="SubjectEntitlement.SubjectType"/>,
/// <see cref="SubjectEntitlement.SubjectId"/>).
///
/// GENERIC, NOT VERTICAL. The kinds are product-neutral Core vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv); a vertical never sees these names — it consumes the resolved effective
/// entitlements and maps them to its own paywall copy in its UI (docs/21 "ArcanOS may display these as ...").
/// The two kinds mirror the two subject surfaces the generic monetization API exposes
/// (csv/mobile_store_api_routes.csv): the current <see cref="User"/> (<c>GET /v1/me/entitlements</c>) and a
/// <see cref="Workspace"/> (<c>GET /v1/workspaces/{workspaceId}/quota-status</c>).
///
/// The kind is persisted by its stable NAME (not its numeric value), so the integers below are only in-memory
/// storage discriminators (persisted by name, like <see cref="EntitlementValueKind"/>, <c>ExportScope</c> and
/// every other enum in the model), carry no ordering meaning and must not be compared with &gt;/&lt;.
/// </summary>
public enum EntitlementSubjectType
{
    /// <summary>
    /// A single USER (identified by their <c>users(id)</c> profile id). The primary subject of premium state:
    /// the "current user" whose effective entitlements <c>GET /v1/me/entitlements</c> returns
    /// (csv/mobile_store_api_routes.csv). A user's premium state spans the deployment (it follows the user's
    /// purchase, not a tenant), so a user-subject assignment is not tenant-scoped — it is keyed by the user id.
    /// </summary>
    User = 1,

    /// <summary>
    /// A single WORKSPACE (identified by its <c>workspaces(id)</c> id). The subject of a workspace-scoped
    /// entitlement such as a hosted-session capability or a workspace quota (csv/mobile_entitlement_catalog.csv
    /// <c>scope</c> column). A workspace id is globally unique, so keying by (<see cref="Workspace"/>,
    /// workspaceId) is unambiguous and never crosses tenants; the caller resolves and authorizes the workspace's
    /// tenant context before assigning or reading its entitlements (the workspace quota-status endpoint and its
    /// membership authorization are the later quota story, CORE-ENTL-003).
    /// </summary>
    Workspace = 2,

    /// <summary>
    /// A single SESSION (identified by its <c>sessions(id)</c> id). The subject of a session-scoped quota — the
    /// <c>session.participant.max</c> participant cap (csv/mobile_entitlement_catalog.csv <c>scope</c> column
    /// <c>session</c>), whose usage is the count of participants admitted to ONE session, so it must be measured
    /// PER SESSION rather than per workspace. Enforced on the participant-join path
    /// (<c>SessionParticipantJoinService.JoinAsync</c>) via <see cref="QuotaEnforcementService"/> (CORE-MON-005),
    /// so the documented free-tier participant cap is a real server-side gate and not a UI-only restriction
    /// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). A session id is globally unique, so keying by
    /// (<see cref="Session"/>, sessionId) is unambiguous and never crosses tenants; the caller resolves and
    /// authorizes the session's tenant/workspace context (the join service's scoped repository lookups) before
    /// consuming its participant quota.
    /// </summary>
    Session = 3,
}
