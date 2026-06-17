// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.Extensions.Caching.Memory;

namespace LiveCore.Api.UnitTests.Persistence;

/// <summary>
/// Unit tests for the transparent caching repository decorators (CORE-PERF-003): the organization, organization-
/// member, workspace-member and user-profile decorators that serve the stable per-request authorization lookups
/// from the shared <see cref="AuthorizationLookupCache"/>.
///
/// Each test drives a decorator over a COUNTING fake inner repository, so it can ASSERT the exact behaviour the
/// story requires:
/// <list type="bullet">
///   <item>repeated lookups by the same principal within the TTL do NOT re-issue the inner query (the cache serves
///   them);</item>
///   <item>a membership change (removal / erasure / tenant deletion) invalidates the cache, so the next lookup
///   re-queries AND now denies — fail-closed;</item>
///   <item>a MISS (no membership / unknown subject) is never cached, so a foreign-tenant / unauthorized denial is
///   always re-checked;</item>
///   <item>the EXACT, non-linear role check is unchanged (a cached membership answers Host vs CoHost correctly from
///   a single underlying query).</item>
/// </list>
/// </summary>
public sealed class AuthorizationCacheDecoratorTests
{
    private static readonly DateTimeOffset _seedTime = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private const string _issuer = "https://issuer.test";

    private static AuthorizationLookupCache CreateCache()
        => new(new MemoryCache(new MemoryCacheOptions()), AuthorizationCacheOptions.Default);

    // ---- Organization-membership decorator (the resolver's authoritative grant) -----------------------------

    [Fact]
    public async Task Organization_member_lookup_is_served_from_cache_then_re_queried_after_removal()
    {
        var organizationId = Guid.CreateVersion7();
        var userProfileId = Guid.CreateVersion7();
        var member = OrganizationMember.Create(organizationId, userProfileId, MembershipRole.Participant, _seedTime);
        var inner = new CountingOrganizationMemberRepository(member);
        var decorator = new CachingOrganizationMemberRepository(inner, CreateCache());

        // Repeated lookups by the same principal within the TTL are served from the cache: one inner query.
        Assert.NotNull(await decorator.FindAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.NotNull(await decorator.FindAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.True(await decorator.IsMemberAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.Equal(1, inner.FindCalls);

        // Removing the membership invalidates the cache; the next lookup re-queries AND now denies (fail-closed).
        await decorator.RemoveAsync(member, CancellationToken.None);
        Assert.Equal(1, inner.RemoveCalls);

        Assert.Null(await decorator.FindAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.Equal(2, inner.FindCalls);
    }

    [Fact]
    public async Task Organization_member_miss_is_never_cached_so_a_non_member_is_always_rechecked()
    {
        var inner = new CountingOrganizationMemberRepository(member: null);
        var decorator = new CachingOrganizationMemberRepository(inner, CreateCache());
        var organizationId = Guid.CreateVersion7();
        var userProfileId = Guid.CreateVersion7();

        Assert.Null(await decorator.FindAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.False(await decorator.IsMemberAsync(organizationId, userProfileId, CancellationToken.None));
        Assert.Null(await decorator.FindAsync(organizationId, userProfileId, CancellationToken.None));

        // A miss is positive-only-not-cached: every call re-checks against the database (fail-closed).
        Assert.Equal(3, inner.FindCalls);
    }

    // ---- Workspace-membership decorator (the per-endpoint role re-queries) -----------------------------------

    [Fact]
    public async Task Workspace_role_probes_share_one_cached_query_and_keep_the_exact_role_check()
    {
        var organizationId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        var userProfileId = Guid.CreateVersion7();
        var member = WorkspaceMember.Create(organizationId, workspaceId, userProfileId, MembershipRole.Host, _seedTime);
        var inner = new CountingWorkspaceMemberRepository(member);
        var decorator = new CachingWorkspaceMemberRepository(inner, CreateCache());

        // The participant-feed pattern: Host then CoHost, then repeat. The EXACT role check is preserved (Host is a
        // member with role Host; CoHost is false), and all probes share ONE underlying membership query.
        Assert.True(await decorator.HasRoleAsync(organizationId, workspaceId, userProfileId, MembershipRole.Host, CancellationToken.None));
        Assert.False(await decorator.HasRoleAsync(organizationId, workspaceId, userProfileId, MembershipRole.CoHost, CancellationToken.None));
        Assert.True(await decorator.HasRoleAsync(organizationId, workspaceId, userProfileId, MembershipRole.Host, CancellationToken.None));
        Assert.True(await decorator.IsMemberAsync(organizationId, workspaceId, userProfileId, CancellationToken.None));
        Assert.Equal(1, inner.FindCalls);

        // Removing the membership invalidates the cache; the next role check re-queries AND denies (fail-closed).
        await decorator.RemoveAsync(member, CancellationToken.None);
        Assert.False(await decorator.HasRoleAsync(organizationId, workspaceId, userProfileId, MembershipRole.Host, CancellationToken.None));
        Assert.Equal(2, inner.FindCalls);
    }

    [Fact]
    public async Task Workspace_role_check_rejects_an_undefined_role_without_touching_the_database()
    {
        var inner = new CountingWorkspaceMemberRepository(member: null);
        var decorator = new CachingWorkspaceMemberRepository(inner, CreateCache());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decorator.HasRoleAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), (MembershipRole)999, CancellationToken.None));

        Assert.Equal(0, inner.FindCalls);
    }

    // ---- Organization-by-slug decorator (the resolver's first lookup) ----------------------------------------

    [Fact]
    public async Task Organization_by_slug_is_served_from_cache_then_re_queried_after_deletion()
    {
        var organization = Organization.Create("acme-co", "Acme", _seedTime);
        var inner = new CountingOrganizationRepository(organization);
        var decorator = new CachingOrganizationRepository(inner, CreateCache());

        Assert.NotNull(await decorator.FindBySlugAsync("acme-co", CancellationToken.None));
        Assert.NotNull(await decorator.FindBySlugAsync("acme-co", CancellationToken.None));
        Assert.Equal(1, inner.FindBySlugCalls);

        // Deleting the tenant invalidates its cache group; the next by-slug lookup re-queries and now denies.
        await decorator.DeleteAsync(organization, CancellationToken.None);
        Assert.Null(await decorator.FindBySlugAsync("acme-co", CancellationToken.None));
        Assert.Equal(2, inner.FindBySlugCalls);
    }

    // ---- User-profile-by-OIDC decorator (the resolver's subject mapping) -------------------------------------

    [Fact]
    public async Task User_profile_by_oidc_is_served_from_cache_then_re_queried_after_erasure()
    {
        const string subject = "subject-a";
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, subject), _seedTime);
        var inner = new CountingUserProfileRepository(profile);
        var decorator = new CachingUserProfileRepository(inner, CreateCache());

        Assert.NotNull(await decorator.FindByOidcIdentityAsync(_issuer, subject, CancellationToken.None));
        Assert.NotNull(await decorator.FindByOidcIdentityAsync(_issuer, subject, CancellationToken.None));
        Assert.Equal(1, inner.FindByOidcCalls);

        // Erasing the profile invalidates the subject's cache group; the next lookup re-queries and now denies.
        await decorator.EraseAsync(profile, CancellationToken.None);
        Assert.Null(await decorator.FindByOidcIdentityAsync(_issuer, subject, CancellationToken.None));
        Assert.Equal(2, inner.FindByOidcCalls);
    }

    // ---- Counting fakes -------------------------------------------------------------------------------------

    private sealed class CountingOrganizationMemberRepository : IOrganizationMemberRepository
    {
        private OrganizationMember? _member;

        public CountingOrganizationMemberRepository(OrganizationMember? member) => _member = member;

        public int FindCalls { get; private set; }

        public int RemoveCalls { get; private set; }

        public Task<OrganizationMember?> FindAsync(Guid organizationId, Guid userProfileId, CancellationToken cancellationToken)
        {
            FindCalls++;
            var match = _member is not null
                && _member.OrganizationId == organizationId
                && _member.UserProfileId == userProfileId
                    ? _member
                    : null;
            return Task.FromResult(match);
        }

        public Task RemoveAsync(OrganizationMember member, CancellationToken cancellationToken)
        {
            RemoveCalls++;
            _member = null;
            return Task.CompletedTask;
        }

        public Task<bool> IsMemberAsync(Guid organizationId, Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OrganizationMemberAddResult> AddAsync(OrganizationMember member, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OrganizationMember?> FindByIdAsync(Guid organizationId, Guid memberId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> CountByRoleAsync(Guid organizationId, MembershipRole role, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> IsSoleOwnerOfAnyOrganizationAsync(Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CountingWorkspaceMemberRepository : IWorkspaceMemberRepository
    {
        private WorkspaceMember? _member;

        public CountingWorkspaceMemberRepository(WorkspaceMember? member) => _member = member;

        public int FindCalls { get; private set; }

        public Task<WorkspaceMember?> FindAsync(Guid organizationId, Guid workspaceId, Guid userProfileId, CancellationToken cancellationToken)
        {
            FindCalls++;
            var match = _member is not null
                && _member.OrganizationId == organizationId
                && _member.WorkspaceId == workspaceId
                && _member.UserProfileId == userProfileId
                    ? _member
                    : null;
            return Task.FromResult(match);
        }

        public Task RemoveAsync(WorkspaceMember member, CancellationToken cancellationToken)
        {
            _member = null;
            return Task.CompletedTask;
        }

        public Task<bool> IsMemberAsync(Guid organizationId, Guid workspaceId, Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> HasRoleAsync(Guid organizationId, Guid workspaceId, Guid userProfileId, MembershipRole role, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WorkspaceMemberAddResult> AddAsync(WorkspaceMember member, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WorkspaceMember?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid memberId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> CountByRoleAsync(Guid organizationId, Guid workspaceId, MembershipRole role, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspaceMember>> ListBySubjectInOrganizationAsync(Guid organizationId, Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CountingOrganizationRepository : IOrganizationRepository
    {
        private Organization? _organization;

        public CountingOrganizationRepository(Organization organization) => _organization = organization;

        public int FindBySlugCalls { get; private set; }

        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
        {
            FindBySlugCalls++;
            var canonical = Organization.CanonicalizeSlug(slug);
            var match = _organization is not null && _organization.Slug == canonical ? _organization : null;
            return Task.FromResult(match);
        }

        public Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
        {
            _organization = null;
            return Task.CompletedTask;
        }

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Organization>> ListByMemberAsync(Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(Guid userProfileId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<OrganizationAddResult> AddWithOwnerAsync(Organization organization, OrganizationMember owner, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class CountingUserProfileRepository : IUserProfileRepository
    {
        private UserProfile? _profile;

        public CountingUserProfileRepository(UserProfile profile) => _profile = profile;

        public int FindByOidcCalls { get; private set; }

        public Task<UserProfile?> FindByOidcIdentityAsync(string issuer, string subjectId, CancellationToken cancellationToken)
        {
            FindByOidcCalls++;
            var match = _profile is not null && _profile.Issuer == issuer && _profile.SubjectId == subjectId
                ? _profile
                : null;
            return Task.FromResult(match);
        }

        public Task EraseAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            _profile = null;
            return Task.CompletedTask;
        }

        public Task<UserProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
