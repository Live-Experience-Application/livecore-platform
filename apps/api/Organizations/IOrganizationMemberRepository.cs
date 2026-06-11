namespace LiveCore.Api.Organizations;

/// <summary>
/// Persistence contract for organization membership (CORE-ID-004). The
/// Organizations module owns the <c>organization_members</c> table; other
/// modules access memberships only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations).
///
/// Every lookup is explicitly tenant-scoped: the caller passes both the
/// organization id and the subject id, and a membership is only ever returned
/// for exactly that (organization, subject) pair. There is deliberately no
/// lookup of "a subject's memberships" without an organization and no lookup
/// that crosses tenants, so a membership in one organization can never be read
/// through another organization's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Resolving the "current" organization or
/// subject from a request is not done here; that is the tenant context
/// resolver story (CORE-ID-005). This contract takes explicit ids.
/// </summary>
public interface IOrganizationMemberRepository
{
    /// <summary>
    /// Finds the membership for exactly the given (organization, subject)
    /// pair, or <see langword="null"/> when the subject has no membership in
    /// that organization. The organization scopes the lookup, so a membership
    /// the subject holds in another organization is never returned.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or subject id is empty. An empty id can never
    /// address a stored membership, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<OrganizationMember?> FindAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the given subject is a member of the given organization (in any
    /// role). Returns <see langword="false"/> when the subject has no
    /// membership in that organization, including when the subject is a member
    /// of another organization only: membership in one tenant is never visible
    /// through another tenant's id (deny-by-default, threat T5). This is the
    /// tenant-scoped membership check later authorization policies build on;
    /// mapping roles to per-action capabilities is a later story, so this does
    /// not interpret the role.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or subject id is empty.
    /// </exception>
    Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new membership. Returns
    /// <see cref="OrganizationMemberAddResult.DuplicateMembership"/> when a
    /// membership for the same (organization, subject) pair already exists
    /// (enforced by the unique database index, so concurrent first-time
    /// callers can never create two standings for one subject in one tenant).
    /// </summary>
    Task<OrganizationMemberAddResult> AddAsync(
        OrganizationMember member,
        CancellationToken cancellationToken);
}
