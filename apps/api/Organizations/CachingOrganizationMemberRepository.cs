// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Persistence;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Caching decorator over <see cref="IOrganizationMemberRepository"/> (CORE-PERF-003). The tenant context resolver
/// runs the organization-membership lookup on EVERY authenticated request before the endpoint (it is the
/// authoritative grant that mints the trusted <see cref="TenantContext"/>). This decorator serves
/// <see cref="FindAsync"/> — and the <see cref="IsMemberAsync"/> built on it — from the shared
/// <see cref="AuthorizationLookupCache"/> so a high request rate by the same principal does not re-issue the
/// identical membership SELECT each time.
///
/// <para>
/// It is a TRANSPARENT decorator over the concrete <see cref="OrganizationMemberRepository"/>, so the resolver and
/// every other caller is unchanged. Only a positive membership is cached (no membership is never cached, so a
/// foreign-tenant / non-member caller is always re-checked, fail-closed); the cached row is read-only (a membership
/// is created and removed, never updated, and the role is therefore stable). REMOVING a membership invalidates the
/// subject's cache group, so the removed member loses cached tenant access on their very next request, fail-closed —
/// the same revocation-on-next-request guarantee the un-cached resolver gave (CORE-LIFE-001). A data-subject erasure
/// or tenant deletion that CASCADE-removes the membership in the database invalidates the same subject/organization
/// groups through the user-profile / organization decorators, so those paths evict this cache too. All other
/// operations delegate straight to the inner repository.
/// </para>
/// </summary>
internal sealed class CachingOrganizationMemberRepository : IOrganizationMemberRepository
{
    private const string _memberKeyPrefix = "auth:org:mem:";

    private readonly IOrganizationMemberRepository _inner;
    private readonly AuthorizationLookupCache _cache;

    public CachingOrganizationMemberRepository(IOrganizationMemberRepository inner, AuthorizationLookupCache cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        _inner = inner;
        _cache = cache;
    }

    /// <inheritdoc />
    public Task<OrganizationMember?> FindAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
        => _cache.GetOrAddAsync(
            MemberKey(organizationId, userProfileId),
            ct => _inner.FindAsync(organizationId, userProfileId, ct),
            static member =>
            [
                AuthorizationLookupCache.OrganizationGroup(member.OrganizationId),
                AuthorizationLookupCache.SubjectGroup(member.UserProfileId),
            ],
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken)
    {
        // Reuse the cached lookup so the deny-by-default / tenant-isolation semantics live in one place and the
        // membership SELECT is served from the cache once per window, exactly like the inner implementation reuses
        // its own FindAsync.
        var member = await FindAsync(organizationId, userProfileId, cancellationToken).ConfigureAwait(false);
        return member is not null;
    }

    /// <inheritdoc />
    public Task<OrganizationMemberAddResult> AddAsync(OrganizationMember member, CancellationToken cancellationToken)
        => _inner.AddAsync(member, cancellationToken);

    /// <inheritdoc />
    public Task<OrganizationMember?> FindByIdAsync(
        Guid organizationId,
        Guid memberId,
        CancellationToken cancellationToken)
        => _inner.FindByIdAsync(organizationId, memberId, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountByRoleAsync(
        Guid organizationId,
        MembershipRole role,
        CancellationToken cancellationToken)
        => _inner.CountByRoleAsync(organizationId, role, cancellationToken);

    /// <inheritdoc />
    public async Task RemoveAsync(OrganizationMember member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        await _inner.RemoveAsync(member, cancellationToken).ConfigureAwait(false);

        // The membership row IS the access grant. Evicting the subject's cache group drops their cached
        // organization membership (and workspace roles), so resolution re-queries and denies on the next request —
        // fail-closed, the moment access is revoked.
        _cache.InvalidateSubject(member.UserProfileId);
    }

    /// <inheritdoc />
    public Task<bool> IsSoleOwnerOfAnyOrganizationAsync(Guid userProfileId, CancellationToken cancellationToken)
        => _inner.IsSoleOwnerOfAnyOrganizationAsync(userProfileId, cancellationToken);

    private static string MemberKey(Guid organizationId, Guid userProfileId)
        => string.Concat(_memberKeyPrefix, organizationId.ToString("N"), ":", userProfileId.ToString("N"));
}
