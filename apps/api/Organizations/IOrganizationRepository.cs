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
    /// Persists a new organization. Returns
    /// <see cref="OrganizationAddResult.DuplicateSlug"/> when an organization
    /// with the same slug already exists (enforced by the unique database
    /// index, so concurrent first-time callers can never create two tenants
    /// for one slug).
    /// </summary>
    Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken);
}
