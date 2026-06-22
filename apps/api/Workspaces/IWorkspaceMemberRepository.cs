// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.Workspaces;

/// <summary>
/// Persistence contract for workspace membership (CORE-WS-002). The Workspaces
/// module owns the <c>workspace_members</c> table; other modules access
/// memberships only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Workspaces module owns "workspace
/// membership" and "workspace-level roles").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the
/// organization id, the workspace id and the subject id, and a membership is
/// only ever returned for exactly that (organization, workspace, subject)
/// triple. The organization boundary is checked before the workspace boundary
/// (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a membership
/// is never returned through a foreign organization's id even when the workspace
/// and subject ids are correct, and never through a foreign workspace's id even
/// when the organization and subject ids are correct. There is deliberately no
/// lookup of "a subject's memberships" without a workspace, no lookup by
/// workspace alone and no lookup that crosses tenants, so a membership in one
/// workspace can never be read through another workspace's id and a membership
/// in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization). Resolving the "current" organization, workspace or subject
/// from a request is not done here; that is the tenant context resolver
/// (CORE-ID-005) and the workspace endpoints (CORE-WS-003). This contract takes
/// explicit ids.
/// </summary>
public interface IWorkspaceMemberRepository
{
    /// <summary>
    /// Finds the membership for exactly the given (organization, workspace,
    /// subject) triple, or <see langword="null"/> when the subject has no
    /// membership in that workspace under that organization. The organization
    /// and workspace both scope the lookup, so a membership the subject holds in
    /// another workspace, or in the same workspace id under a different
    /// organization, is never returned (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or subject id is empty. An empty id can
    /// never address a stored membership, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<WorkspaceMember?> FindAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the given subject is a member of the given workspace under the
    /// given organization (in any role). Returns <see langword="false"/> when
    /// the subject has no membership in that workspace, including when the
    /// subject is a member of another workspace only, or of the same workspace
    /// id under another organization only: membership in one workspace/tenant is
    /// never visible through another workspace's or tenant's id
    /// (deny-by-default, threat T5). This is the scoped membership check later
    /// authorization policies build on; mapping roles to per-action
    /// capabilities is a later story (CORE-WS-005), so this does not interpret
    /// the role.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or subject id is empty.
    /// </exception>
    Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the given subject holds exactly the given generic role in the
    /// given workspace under the given organization. Returns
    /// <see langword="false"/> when the subject has no membership in that
    /// workspace, or holds a different role: the authorization matrix is
    /// non-linear, so a "higher" or "lower" role never satisfies the check, and
    /// a membership in a foreign workspace or tenant is never consulted
    /// (deny-by-default, threat T5; docs/06_AUTHORIZATION_MATRIX.md). The role
    /// is matched exactly; it is never interpreted into a capability here.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or subject id is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    Task<bool> HasRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid userProfileId,
        MembershipRole role,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new membership. Returns
    /// <see cref="WorkspaceMemberAddResult.DuplicateMembership"/> when a
    /// membership for the same (workspace, subject) pair already exists
    /// (enforced by the unique database index, so concurrent first-time callers
    /// can never create two standings for one subject in one workspace).
    /// </summary>
    Task<WorkspaceMemberAddResult> AddAsync(
        WorkspaceMember member,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the membership with exactly the given surrogate id WITHIN the given (organization, workspace)
    /// scope, or <see langword="null"/> when no such membership exists there (CORE-LIFE-001). The member
    /// removal route addresses a member by its id, but a membership is only ever returned when it belongs to
    /// exactly the resolved tenant AND workspace: a membership id that exists in another workspace, or in the
    /// same workspace id under a different organization, is never returned, so a member outside the caller's
    /// resolved tenant/workspace can never be addressed or probed for (threats T1/T5). The organization
    /// boundary leads the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or member id is empty. An empty id can never address a stored
    /// membership, so the lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<WorkspaceMember?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid memberId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts the memberships holding exactly the given generic role in the given workspace under the given
    /// organization (CORE-LIFE-001). It backs the last-Owner invariant: a workspace must not be left without
    /// an Owner, so the removal command refuses to remove the sole Owner. The role is matched exactly; the
    /// authorization matrix is non-linear, so this is never an ordering comparison, and the count is
    /// tenant- and workspace-scoped so a foreign workspace's or tenant's members are never counted
    /// (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    Task<int> CountByRoleAsync(
        Guid organizationId,
        Guid workspaceId,
        MembershipRole role,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes (hard-deletes) the given membership, revoking the subject's standing in the workspace
    /// (CORE-LIFE-001). The membership row IS the access grant — every workspace-scoped authorization reads
    /// it (<see cref="IsMemberAsync"/>/<see cref="FindAsync"/>) — so deleting it revokes access on the
    /// subject's very next request, fail-closed. The append-only audit log records the removal as a durable
    /// fact and is never cascade-erased with the row (the audit references are recorded facts, not foreign
    /// keys). The caller is responsible for guarding the last-Owner invariant before calling this.
    /// </summary>
    /// <exception cref="ArgumentNullException">The member is null.</exception>
    Task RemoveAsync(
        WorkspaceMember member,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every workspace membership the given subject holds WITHIN the given organization (tenant), oldest
    /// first, for the data-subject access/portability export (CORE-PRIV-004, GDPR Art.15/20). It is the only
    /// lookup on this contract keyed by the subject across a whole tenant (every other lookup additionally pins a
    /// single workspace), and it is deliberately TENANT-SCOPED: the export under
    /// <c>organizations/{slug}/members/{memberId}/personal-data-export</c> is authorized within ONE tenant, so it
    /// returns only the subject's workspace memberships in THAT tenant. The predicate LEADS with
    /// <c>organization_id</c> and matches the subject, so a membership the subject holds in another tenant is
    /// never returned even when the surrogate ids would otherwise be addressable, and the subject's memberships in
    /// other tenants are reached only through that tenant's own export route (threat T5/T1 in
    /// docs/07_SECURITY_THREAT_MODEL.md). The read is tracking-free and ordered by the time-ordered surrogate id
    /// (UUIDv7), provider-independent (SQLite cannot ORDER BY a DateTimeOffset), matching the other repositories'
    /// ordering convention.
    /// </summary>
    /// <param name="organizationId">The tenant the export is authorized in (required, non-empty).</param>
    /// <param name="userProfileId">The data subject's user-profile id (required, non-empty).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject's workspace memberships in the tenant (empty when the subject has none there).</returns>
    /// <exception cref="ArgumentException">The organization id or subject id is empty.</exception>
    Task<IReadOnlyList<WorkspaceMember>> ListBySubjectInOrganizationAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the given workspace's membership ROSTER under the given organization
    /// (CORE-WSM-001), oldest first, as the audience-safe <see cref="WorkspaceMemberRosterEntry"/> projection
    /// the administration member-roster read returns. It is the read that finally makes a workspace's
    /// membership ids enumerable to an administrator (so a host can drive <see cref="RemoveAsync"/> /
    /// <c>removeMember</c> with a returned membership id), distinct from the per-subject
    /// <see cref="ListBySubjectInOrganizationAsync"/> the personal-data export uses.
    ///
    /// The read is TENANT- and WORKSPACE-scoped: the predicate LEADS with <c>organization_id</c> (the
    /// organization boundary checked before the workspace boundary) and matches the workspace, so a membership in
    /// another workspace, or in the same workspace id under a different organization, is never returned (threats
    /// T1/T5 in docs/07_SECURITY_THREAT_MODEL.md). It JOINS the audience-safe display metadata of the subject's
    /// <c>users</c> profile READ-ONLY — selecting ONLY the optional display name, never the profile's email,
    /// token or any other column — so the projection is data-minimized at the query (threats T6/T7). The read is
    /// tracking-free (it never mutates) and ordered oldest-first by the time-ordered surrogate id (UUIDv7),
    /// provider-independent because SQLite cannot ORDER BY a DateTimeOffset, matching the other repositories'
    /// ordering convention. It is bounded by <paramref name="skip"/>/<paramref name="take"/> so a single
    /// response can never materialize an unbounded array (threat T9; CORE-DX-003).
    /// </summary>
    /// <param name="organizationId">The tenant the roster is read in (required, non-empty).</param>
    /// <param name="workspaceId">The workspace whose members are listed (required, non-empty).</param>
    /// <param name="skip">Zero-based offset of the first row to return (non-negative).</param>
    /// <param name="take">Maximum number of rows to return (at least one).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page of audience-safe roster entries (empty when the workspace has no members on the page).</returns>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The skip is negative or the take is less than one.</exception>
    Task<IReadOnlyList<WorkspaceMemberRosterEntry>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
        CancellationToken cancellationToken);
}
