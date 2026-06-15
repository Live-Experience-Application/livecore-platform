using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Entities;

/// <summary>
/// The host-vs-audience VISIBILITY SPLIT for entity search (CORE-ENT-005, retrofitted by CORE-API-006
/// to apply the real audience filter) — the pure, role-only decision of which VIEW of a workspace's
/// entities a caller takes: the HOST-ONLY-CONTENT view (every matching entity), the
/// AUDIENCE-PARTICIPANT view (the visibility-filtered set), or the fail-closed empty view. It is the
/// direct analogue of <see cref="Scenes.SceneProjection.ReceivesHostShape"/> for the "View host-only
/// content" row of docs/06_AUTHORIZATION_MATRIX.md.
///
/// WHY THIS IS NOT A VISIBILITY ENGINE. The central Visibility module owns "visibility rules",
/// "audience calculations", "preview-as-participant", <c>CanViewResource</c> and
/// <c>GetVisibleResourcesForParticipant</c> (docs/05_MODULE_CONTRACTS.md), and the architecture is
/// explicit that "entity visibility is not computed ad hoc in many places"
/// (docs/02_ARCHITECTURE.md) and "Do not duplicate visibility logic elsewhere"
/// (docs/05_MODULE_CONTRACTS.md). So this class deliberately holds NO per-entity visibility-rule
/// evaluation, NO audience math and NO reveal state. It makes only the coarse, ROLE-level
/// authorization decision — "which view of entities does this workspace role take?" — that gates
/// WHICH path the search takes. The actual per-entity audience-visible decision is the central
/// Visibility module's (CORE-VIS-005 <see cref="VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>),
/// which <see cref="EntitySearchService"/> routes each candidate through on the audience path — never
/// re-derived here.
///
/// Role -> view mapping, decided STRICTLY from the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md (csv/authorization_matrix.csv): Owner/Admin/Host/CoHost =
/// <c>yes</c> get the host-only-content view; Participant/Observer = <c>no</c> get the audience view;
/// Auditor = <c>audit-only</c> does NOT.
/// <list type="bullet">
///   <item>Owner, Admin, Host, CoHost -> host-only-content view (every matching workspace entity).
///   These are the roles the matrix grants "View host-only content" = <c>yes</c>; entities are
///   host-prepared content, so a host-capable role sees the full matching set.</item>
///   <item>Participant, Observer -> audience view. The matrix grants them "View host-only content" =
///   <c>no</c> and "View participant-visible content" = "if visible"; what is visible is computed
///   server-side by the central Visibility engine PER PARTICIPANT, so the audience path keeps exactly
///   the entities revealed to the calling participant (an audience-wide visible rule, or a visible
///   rule scoped to exactly them). An audience role with no identified participant has no participant
///   to compute for, so it fails closed to the empty view.</item>
///   <item>Auditor -> empty view. The matrix grants Auditor "View host-only content" =
///   <c>audit-only</c>, NOT <c>yes</c>: an auditor's access to sensitive content is an explicit
///   audit-trail concern (the later Audit epic), not a live host-content search grant. The
///   authorization principle "Audit roles may view metadata but should not automatically view
///   sensitive content unless explicitly allowed" (docs/06) is honored by excluding Auditor from BOTH
///   the host and the audience view here (it is neither a host-content role nor an audience role).
///   This is the deliberate distinction from <see cref="Scenes.SceneProjection"/>, which gives
///   Auditor the host SHAPE because that projector exposes only scene METADATA ("View workspace
///   metadata" = <c>yes</c> for Auditor) and never content — whereas an entity IS content.</item>
/// </list>
///
/// <see cref="MembershipRole"/> is NON-LINEAR (docs/06 is non-linear; the enum's integer values are
/// storage discriminators only and must never be compared with &gt;/&lt;), so each decision is an
/// EXACT set-membership check, never an ordering comparison. Any value outside both sets — the audit
/// role and any undefined enum value — fails closed to the empty view (deny-by-default: an
/// unrecognized role is never granted the host-only-content view and is not an audience role; threats
/// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md).
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
    /// (<c>audit-only</c>) and any undefined value — is denied the host view. The classification is
    /// EXACT set membership, never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ViewsHostOnlyContent(MembershipRole role)
        => VisibilityRoles.ViewsHostOnlyContent(role);

    /// <summary>
    /// Whether the given workspace role takes the AUDIENCE-PARTICIPANT view of entity search (the set
    /// the central Visibility engine reveals to the calling participant) rather than the host view.
    /// This DELEGATES to the central Visibility module's canonical audience-role classification
    /// (<see cref="VisibilityRoles.IsAudienceRole"/>, CORE-VIS-002) — Participant and Observer — so the
    /// audience role set is defined in ONE place and never duplicated (docs/05_MODULE_CONTRACTS.md).
    /// The host roles are handled by <see cref="ViewsHostOnlyContent"/>; the audit role and any
    /// undefined value are in NEITHER set and fail closed to the empty view. A caller still needs an
    /// identified participant to compute the per-participant visibility, which
    /// <see cref="EntitySearchService"/> requires before taking this view. The classification is EXACT
    /// set membership, never a &gt;/&lt; comparison.
    /// </summary>
    public static bool ViewsAudienceFilteredContent(MembershipRole role)
        => VisibilityRoles.IsAudienceRole(role);
}
