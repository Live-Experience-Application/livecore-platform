namespace LiveCore.Api.Organizations;

/// <summary>
/// Persistence contract for the organization tenant root (CORE-ID-003). The
/// Organizations module owns the <c>organizations</c> table; other modules
/// access organizations only through this contract or the module's
/// application services (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations).
///
/// Lookups address one organization by exactly one of its two keys: the
/// surrogate <c>id</c> or the canonical <c>slug</c>. Both are exact matches
/// and each addresses at most one tenant; there is no broad or fuzzy search,
/// so a lookup can never return a foreign tenant's row (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public interface IOrganizationRepository
{
    /// <summary>
    /// Finds the organization with exactly the given surrogate id, or
    /// <see langword="null"/> when no such organization exists. An empty id
    /// addresses nothing and is rejected.
    /// </summary>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the organization with exactly the given canonical slug, or
    /// <see langword="null"/> when no such organization exists. The slug is
    /// canonicalized before matching so callers may pass any casing/whitespace
    /// variant of a valid slug; matching against stored values is then exact
    /// (ordinal, case-sensitive).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The slug violates the slug invariants. An invalid slug can never
    /// address a stored organization, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the organizations the given subject is a member of (in any role),
    /// ordered by the organizations' time-ordered surrogate id. This is the
    /// subject's own "my tenants" read backing
    /// <c>GET /api/v1/organizations</c> (csv/api_routes.csv: "Only
    /// organizations user belongs to"). It is deliberately distinct from the
    /// tenant-scoped membership lookups on
    /// <see cref="IOrganizationMemberRepository"/>: those forbid resolving a
    /// subject's memberships without an organization (so one tenant's data is
    /// never reached through another's id), whereas this returns only the
    /// organizations the subject is genuinely a member of — never any tenant
    /// the subject does not belong to (deny-by-default; threat T5 in
    /// docs/07_SECURITY_THREAT_MODEL.md). It does not interpret the membership
    /// role and applies no token-claim filter; the caller intersects the result
    /// with the principal's organization claims so the listing matches what the
    /// tenant resolver would grant per organization.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The subject id is empty. An empty id can never address a stored
    /// membership, so the lookup is rejected instead of silently returning an
    /// empty list.
    /// </exception>
    Task<IReadOnlyList<Organization>> ListByMemberAsync(Guid userProfileId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new organization. Returns
    /// <see cref="OrganizationAddResult.DuplicateSlug"/> when an organization
    /// with the same slug already exists (enforced by the unique database
    /// index, so concurrent first-time callers can never create two tenants
    /// for one slug).
    /// </summary>
    Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new organization together with its founding owner membership
    /// ATOMICALLY, in a single transaction (the create-tenant operation behind
    /// <c>POST /api/v1/organizations</c>: "Creates organization and Owner
    /// membership", csv/api_routes.csv). The Organizations module owns both the
    /// <c>organizations</c> and <c>organization_members</c> tables
    /// (docs/05_MODULE_CONTRACTS.md), so the tenant and its founding owner are
    /// committed together: a tenant is never left without an owner (which would
    /// be permanently unreachable, since the tenant resolver requires a
    /// membership), and a duplicate slug rolls the membership back with it.
    /// Returns <see cref="OrganizationAddResult.DuplicateSlug"/> when an
    /// organization with the same slug already exists; in that case NOTHING is
    /// persisted — the caller is never added to a pre-existing tenant, so a
    /// create can never escalate into membership of someone else's organization
    /// (threats T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The owner membership does not belong to the organization being created.
    /// </exception>
    Task<OrganizationAddResult> AddWithOwnerAsync(
        Organization organization,
        OrganizationMember owner,
        CancellationToken cancellationToken);
}
