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

    /// <summary>
    /// Finds the membership with exactly the given surrogate id WITHIN the given organization, or
    /// <see langword="null"/> when no such membership exists there (CORE-LIFE-001). The member removal route
    /// addresses a member by its id, but a membership is only ever returned when it belongs to exactly the
    /// resolved tenant: a membership id that exists in another organization is never returned, so a member
    /// outside the caller's resolved tenant can never be addressed or probed for (threats T1/T5). This is the
    /// tenant-scoped, by-id sibling of <see cref="FindAsync"/> (which addresses a member by its subject).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or member id is empty. An empty id can never address a stored membership, so the
    /// lookup is rejected instead of silently returning nothing.
    /// </exception>
    Task<OrganizationMember?> FindByIdAsync(
        Guid organizationId,
        Guid memberId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts the memberships holding exactly the given generic role in the given organization
    /// (CORE-LIFE-001). It backs the last-Owner invariant: a tenant must never be left without an Owner (an
    /// ownerless organization is permanently unreachable, since the tenant resolver requires a membership),
    /// so the removal command refuses to remove the sole Owner. The role is matched exactly; the
    /// authorization matrix is non-linear, so this is never an ordering comparison, and the count is
    /// tenant-scoped so a foreign tenant's members are never counted (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The role is not a defined <see cref="MembershipRole"/>.
    /// </exception>
    Task<int> CountByRoleAsync(
        Guid organizationId,
        MembershipRole role,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes (hard-deletes) the given membership, revoking the subject's standing in the organization
    /// (CORE-LIFE-001). The membership row IS the access grant — the tenant context resolver requires it
    /// (<see cref="IsMemberAsync"/>/<see cref="FindAsync"/>) — so deleting it revokes the subject's tenant
    /// access on their very next request, fail-closed. The append-only audit log records the removal as a
    /// durable fact and is never cascade-erased with the row (the audit references are recorded facts, not
    /// foreign keys). The caller is responsible for guarding the last-Owner invariant before calling this.
    /// </summary>
    /// <exception cref="ArgumentNullException">The member is null.</exception>
    Task RemoveAsync(
        OrganizationMember member,
        CancellationToken cancellationToken);
}
