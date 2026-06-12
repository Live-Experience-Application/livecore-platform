using LiveCore.Api.Organizations;

namespace LiveCore.Api.Visibility;

/// <summary>
/// Canonical, server-side classification of a workspace <see cref="MembershipRole"/> for the
/// CONTENT-visibility rows of docs/06_AUTHORIZATION_MATRIX.md — the single source of truth the
/// Visibility module (THE central security module, docs/05_MODULE_CONTRACTS.md) exposes so that
/// "entity visibility is not computed ad hoc in many places" (docs/02_ARCHITECTURE.md) and
/// visibility logic is not "duplicate[d] elsewhere" (docs/05_MODULE_CONTRACTS.md). The
/// <see cref="VisibilityPolicy"/> decision (CORE-VIS-002) is built on these predicates, and the
/// Entities module's entity-search split (<c>EntitySearchVisibility</c>, CORE-ENT-005) now delegates
/// to them rather than hardcoding its own copy of the host-content role set.
///
/// The two predicates partition the role set EXACTLY as the matrix's two content rows do:
/// <list type="bullet">
///   <item><see cref="ViewsHostOnlyContent"/> — the "View host-only content" = <c>yes</c> roles:
///   Owner, Admin, Host, CoHost. They may see a resource whether it is hidden or visible.</item>
///   <item><see cref="IsAudienceRole"/> — the roles whose "View participant-visible content" is
///   "if visible": Participant, Observer. They may see a resource ONLY when a visibility rule makes
///   it visible to the audience.</item>
/// </list>
/// Every other value — the audit role Auditor (the matrix grants it only <c>audit-only</c> on both
/// content rows, an explicit audit-trail concern, never a live content grant — "Audit roles ... should
/// not automatically view sensitive content unless explicitly allowed", docs/06) and any undefined
/// enum value — is in NEITHER set, so the policy denies it by default (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// <see cref="MembershipRole"/> is NON-LINEAR (docs/06 is non-linear; the enum's integer values are
/// storage discriminators only and must never be compared with &gt;/&lt;), so both predicates are
/// EXACT set-membership checks, never ordering comparisons.
/// </summary>
internal static class VisibilityRoles
{
    /// <summary>
    /// Whether the given workspace role may view HOST-ONLY content — the "View host-only content" =
    /// <c>yes</c> roles of docs/06_AUTHORIZATION_MATRIX.md: Owner, Admin, Host, CoHost. Such a role
    /// sees a resource regardless of its visibility state (hidden or visible). Every other role
    /// (the audience roles, the audit role, any undefined value) returns <see langword="false"/>.
    /// </summary>
    public static bool ViewsHostOnlyContent(MembershipRole role)
        => role is MembershipRole.Owner
            or MembershipRole.Admin
            or MembershipRole.Host
            or MembershipRole.CoHost;

    /// <summary>
    /// Whether the given workspace role is an AUDIENCE role whose "View participant-visible content"
    /// is "if visible" (docs/06_AUTHORIZATION_MATRIX.md): Participant or Observer. Such a role sees a
    /// resource only when a visibility rule makes it visible to the audience; it never sees host-only
    /// content. Every other role (the host roles, the audit role, any undefined value) returns
    /// <see langword="false"/> — the host roles are handled by <see cref="ViewsHostOnlyContent"/>,
    /// and the audit/undefined roles are denied by default.
    /// </summary>
    public static bool IsAudienceRole(MembershipRole role)
        => role is MembershipRole.Participant
            or MembershipRole.Observer;
}
