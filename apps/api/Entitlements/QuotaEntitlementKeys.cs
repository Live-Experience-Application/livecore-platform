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

    /// <summary>
    /// Caps a session's participant count (docs/21 generic key <c>session.participant.max</c>;
    /// csv/mobile_entitlement_catalog.csv scope <c>session</c>, free value 4). Enforced on the participant-join
    /// path (<c>SessionParticipantJoinService.JoinAsync</c>) for the <see cref="EntitlementSubjectType.Session"/>
    /// subject the participant joins, and released when a participant leaves
    /// (<c>SessionParticipantLeaveService.LeaveAsync</c>), so the count reflects the session's CURRENT participants
    /// and a free session cannot admit more participants than its plan allows (CORE-MON-005). This is the headline
    /// free-tier participant gate the docs say Core exists to enforce server-side rather than leave to the UI
    /// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). The atomic check-and-consume (CORE-CONC-004) makes
    /// concurrent joins unable to exceed the cap.
    /// </summary>
    public const string SessionParticipantMax = "session.participant.max";

    /// <summary>
    /// Caps a workspace's total stored asset size in BYTES (docs/21 generic key <c>asset.storage.bytes.max</c>;
    /// csv/mobile_entitlement_catalog.csv, a <see cref="QuotaUnit.Bytes"/> quota). Enforced on the asset
    /// upload-intent path (<c>AssetUploadIntentService.CreateAsync</c>) for the
    /// <see cref="EntitlementSubjectType.Workspace"/> subject the asset belongs to: the client-declared object
    /// size is atomically check-and-consumed against the workspace's granted storage quota, so an upload is
    /// rejected once the workspace would exceed its plan storage limit (CORE-MON-006). The consumed bytes are
    /// released when the asset is deleted (<c>AssetDeletionService.DeleteAsync</c>), so the recorded usage reflects
    /// the workspace's CURRENT stored bytes rather than a lifetime total — the storage analogue of how
    /// <see cref="SessionActiveMax"/> is consumed at start and released at end. The atomic check-and-consume
    /// (CORE-CONC-004) makes concurrent uploads unable to exceed the cap, so the documented free-tier storage cap
    /// is a real server-side gate and not a UI-only restriction
    /// (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Free limits cannot be bypassed by clients"). The
    /// workspace is the subject the workspace quota-status route already surfaces (CORE-ENTL-003), exactly like
    /// <see cref="SessionActiveMax"/>.
    /// </summary>
    public const string AssetStorageBytesMax = "asset.storage.bytes.max";
}
