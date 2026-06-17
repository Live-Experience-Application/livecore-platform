// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Caching decorator over <see cref="IOrganizationRepository"/> (CORE-PERF-003). The tenant context resolver runs
/// the organization-by-slug lookup on EVERY authenticated request before the endpoint, and the slug is one of the
/// most stable values in the system. This decorator serves <see cref="FindBySlugAsync"/> from the shared
/// <see cref="AuthorizationLookupCache"/> so a high request rate by the same (or any) caller does not re-issue the
/// identical SELECT each time, and invalidates the tenant on deletion.
///
/// <para>
/// It is a TRANSPARENT decorator: it is registered as <see cref="IOrganizationRepository"/> wrapping the concrete
/// <see cref="OrganizationRepository"/>, so the resolver and every other caller is unchanged. Only the positive
/// by-slug result is cached (a missing organization is never cached, so a non-existent / foreign tenant is always
/// re-checked, fail-closed); the cached row is read-only (an organization is created and deleted, never updated),
/// and deleting the tenant invalidates its cache group — which also evicts every membership cached against it.
/// All other operations delegate straight to the inner repository.
/// </para>
/// </summary>
internal sealed class CachingOrganizationRepository : IOrganizationRepository
{
    private const string _slugKeyPrefix = "auth:org:slug:";

    private readonly IOrganizationRepository _inner;
    private readonly AuthorizationLookupCache _cache;

    public CachingOrganizationRepository(IOrganizationRepository inner, AuthorizationLookupCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        => _inner.FindByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        // Canonicalize for a stable key. CanonicalizeSlug throws ArgumentException for a non-slug value exactly as
        // the inner contract documents, so an invalid slug still fails the same way (and is never cached).
        var canonicalSlug = Organization.CanonicalizeSlug(slug);
        return _cache.GetOrAddAsync(
            string.Concat(_slugKeyPrefix, canonicalSlug),
            ct => _inner.FindBySlugAsync(canonicalSlug, ct),
            static organization => [AuthorizationLookupCache.OrganizationGroup(organization.Id)],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Organization>> ListByMemberAsync(Guid userProfileId, CancellationToken cancellationToken)
        => _inner.ListByMemberAsync(userProfileId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(
        Guid userProfileId,
        CancellationToken cancellationToken)
        => _inner.ListMembershipsByMemberAsync(userProfileId, cancellationToken);

    /// <inheritdoc />
    public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
        => _inner.AddAsync(organization, cancellationToken);

    /// <inheritdoc />
    public Task<OrganizationAddResult> AddWithOwnerAsync(
        Organization organization,
        OrganizationMember owner,
        CancellationToken cancellationToken)
        => _inner.AddWithOwnerAsync(organization, owner, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);

        await _inner.DeleteAsync(organization, cancellationToken).ConfigureAwait(false);

        // Deleting the tenant root CASCADE-removes its memberships in the database; invalidating the organization
        // group evicts the cached by-slug lookup AND every membership/role cached against this organization, so a
        // request to the deleted tenant re-queries and fails closed on its next attempt.
        _cache.InvalidateOrganization(organization.Id);
    }
}
