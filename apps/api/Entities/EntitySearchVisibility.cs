using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Entities;

/// <summary>
/// The host-vs-audience VISIBILITY SPLIT for entity search (CORE-ENT-005, the last story of the
/// "Entity System and Templates" epic) — the pure, role-only decision of whether a caller may see
/// the HOST-ONLY-CONTENT view of a workspace's entities (every matching entity) or only the
/// AUDIENCE view (the visibility-filtered set). It is the direct analogue of
/// <see cref="Scenes.SceneProjection.ReceivesHostShape"/> for the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md.
///
/// WHY THIS IS NOT A VISIBILITY ENGINE. The central Visibility module owns "visibility rules",
/// "audience calculations", "preview-as-participant", <c>CanViewResource</c> and
/// <c>GetVisibleResourcesForParticipant</c> (docs/05_MODULE_CONTRACTS.md), and the architecture is
/// explicit that "entity visibility is not computed ad hoc in many places"
/// (docs/02_ARCHITECTURE.md) and "Do not duplicate visibility logic elsewhere"
/// (docs/05_MODULE_CONTRACTS.md). So this class deliberately holds NO per-entity visibility-rule
/// evaluation, NO audience math and NO reveal state. It makes only the coarse, ROLE-level
/// authorization decision — "does this workspace role get the host-only-content view of entities?"
/// — that gates WHICH path the search takes. Computing the actual audience-visible subset of
/// entities for a non-host caller is the later CORE-VIS-* epic, to which the audience path is
/// deferred (it is empty for now, fail-closed, exactly like the CORE-SES-005 participant-visible
/// feed skeleton).
///
/// Role -> view mapping, decided STRICTLY from the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md (csv/authorization_matrix.csv): Owner/Admin/Host/CoHost =
/// <c>yes</c> get the host-only-content view; Participant/Observer = <c>no</c> and Auditor =
/// <c>audit-only</c> do NOT.
/// <list type="bullet">
///   <item>Owner, Admin, Host, CoHost -> host-only-content view (every matching workspace entity).
///   These are the roles the matrix grants "View host-only content" = <c>yes</c>; entities are
///   host-prepared content, so a host-capable role sees the full matching set.</item>
///   <item>Participant, Observer -> audience view (visibility-filtered, empty for now). The matrix
///   grants them "View host-only content" = <c>no</c> and "View participant-visible content" = "if
///   visible"; what is visible is computed server-side by the Visibility engine (CORE-VIS), which
///   does not exist yet, so the audience-visible set is legitimately empty (fail-closed).</item>
///   <item>Auditor -> audience view (empty). The matrix grants Auditor "View host-only content" =
///   <c>audit-only</c>, NOT <c>yes</c>: an auditor's access to sensitive content is an explicit
///   audit-trail concern (the later Audit epic), not a live host-content search grant. The
///   authorization principle "Audit roles may view metadata but should not automatically view
///   sensitive content unless explicitly allowed" (docs/06) is honored by excluding Auditor here.
///   This is the deliberate distinction from <see cref="Scenes.SceneProjection"/>, which gives
///   Auditor the host SHAPE because that projector exposes only scene METADATA ("View workspace
///   metadata" = <c>yes</c> for Auditor) and never content — whereas an entity IS content.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is NON-LINEAR (docs/06 is non-linear; the enum's integer values are
/// storage discriminators only and must never be compared with &gt;/&lt;), so the decision is an
/// EXACT set-membership check over the host-content roles, never an ordering comparison. Any value
/// outside that set — the audience roles, the audit role and any undefined enum value — fails closed
/// to the audience view (deny-by-default: an unrecognized role is never granted the host-only
/// content view; threats T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
internal static class EntitySearchVisibility
{
    /// <summary>
    /// Whether the given workspace role receives the HOST-ONLY-CONTENT view of entity search (every
    /// matching workspace entity) rather than the audience (visibility-filtered) view. This DELEGATES
    /// to the central Visibility module's canonical "View host-only content" classification
    /// (<see cref="VisibilityRoles.ViewsHostOnlyContent"/>, CORE-VIS-002) so the host-content role set
    /// (Owner/Admin/Host/CoHost) is defined in ONE place and never duplicated
    /// (docs/05_MODULE_CONTRACTS.md: visibility logic is not duplicated elsewhere;
    /// docs/02_ARCHITECTURE.md). Every other role — Participant and Observer (audience), Auditor
    /// (<c>audit-only</c>) and any undefined value — is denied the host view and falls closed to the
    /// audience view. The classification is EXACT set membership, never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ViewsHostOnlyContent(MembershipRole role)
        => VisibilityRoles.ViewsHostOnlyContent(role);
}
