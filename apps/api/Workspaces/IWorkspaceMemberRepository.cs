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
}
