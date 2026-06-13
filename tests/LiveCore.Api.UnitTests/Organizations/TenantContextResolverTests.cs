using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Unit tests for <see cref="TenantContextResolver"/> (CORE-ID-005), the single
/// chokepoint that turns an authenticated principal plus a target organization
/// into a trusted <see cref="TenantContext"/> or a fail-closed denial.
///
/// The tests drive the resolver's decision logic through in-memory fakes of the
/// organization, user profile and membership repositories, so every rule is
/// exercised in isolation without a database. The resolver enforces BOTH the
/// organization CLAIM (the token-asserted tenant boundary, CORE-ID-001) and the
/// persisted MEMBERSHIP (the authoritative grant, CORE-ID-004), in that order.
///
/// The negative foreign-tenant and negative authorization cases below are
/// mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md): a principal with no membership in the
/// target organization is denied; a member of organization A is denied a
/// context for organization B; a principal whose organization claim does not
/// include the target is denied before any data is read; an unknown subject is
/// denied; an unknown organization is denied; and the resolved context never
/// carries a foreign organization's id, subject or role.
/// </summary>
public sealed class TenantContextResolverTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _otherSubject = "11111111-2222-3333-4444-555555555555";
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeOrganizationRepository _organizations = new();
    private readonly FakeUserProfileRepository _userProfiles = new();
    private readonly FakeOrganizationMemberRepository _members = new();
    private readonly TenantContextResolver _resolver;

    public TenantContextResolverTests()
    {
        _resolver = new TenantContextResolver(_organizations, _userProfiles, _members);
    }

    private static OidcPrincipal CreateUserPrincipal(
        string issuer = _issuer,
        string subjectId = _subject,
        params string[] organizationClaims)
        => new(PrincipalType.User, issuer, subjectId, organizationClaims: organizationClaims);

    private Organization SeedOrganization(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        _organizations.Organizations.Add(organization);
        return organization;
    }

    private UserProfile SeedUser(string issuer = _issuer, string subjectId = _subject)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId),
            _createdAt);
        _userProfiles.Profiles.Add(profile);
        return profile;
    }

    private OrganizationMember SeedMembership(Guid organizationId, Guid userProfileId, MembershipRole role)
    {
        var member = OrganizationMember.Create(organizationId, userProfileId, role, _createdAt);
        _members.Members.Add(member);
        return member;
    }

    [Fact]
    public async Task Resolves_a_trusted_context_when_claim_organization_and_membership_all_match()
    {
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Host);
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.None, result.Error);
        Assert.NotNull(result.Context);
        Assert.Equal(organization.Id, result.Context.OrganizationId);
        Assert.Equal(_slugA, result.Context.OrganizationSlug);
        Assert.Equal(user.Id, result.Context.UserProfileId);
        Assert.Equal(MembershipRole.Host, result.Context.Role);
        Assert.True(result.Context.HasRole(MembershipRole.Host));
        Assert.False(result.Context.HasRole(MembershipRole.Owner));
    }

    [Fact]
    public async Task Accepts_a_non_canonical_slug_and_resolves_against_the_canonical_tenant()
    {
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Admin);
        // The token claim carries the canonical slug; the requested target uses
        // mixed casing and whitespace. Canonicalization must line them up.
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, "  NorthWind-Labs  ", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(organization.Id, result.Context!.OrganizationId);
    }

    [Fact]
    public async Task Denies_a_service_account_principal()
    {
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Owner);
        var serviceAccount = new OidcPrincipal(
            PrincipalType.ServiceAccount,
            _issuer,
            _subject,
            organizationClaims: [_slugA]);

        var result = await _resolver.ResolveAsync(serviceAccount, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Context);
        Assert.Equal(TenantContextResolutionError.NotAUser, result.Error);
    }

    [Fact]
    public async Task Denies_when_the_organization_claim_does_not_include_the_target()
    {
        // Mandatory negative authorization test (threat T5): the subject is a
        // real, entitled member of the target organization, but the token does
        // not assert that tenant. The claim is checked first and denies before
        // any membership is consulted; the resolver must not touch the tenant's
        // data when the token is not scoped to it.
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Owner);
        // Claim asserts a different tenant only.
        var principal = CreateUserPrincipal(organizationClaims: _slugB);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.ClaimMismatch, result.Error);
        // Defence in depth: denial happened on the claim, before the database.
        Assert.Equal(0, _organizations.FindBySlugCallCount);
        Assert.Equal(0, _members.FindCallCount);
    }

    [Fact]
    public async Task Denies_when_the_principal_carries_no_organization_claims()
    {
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Owner);
        var principal = CreateUserPrincipal();

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.ClaimMismatch, result.Error);
    }

    [Fact]
    public async Task Claim_match_is_case_sensitive_so_a_differently_cased_claim_is_denied()
    {
        // The organization claim is matched exactly (ordinal, case-sensitive)
        // against the canonical slug; a different-cased claim value never
        // crosses the tenant boundary (threat T5; OidcPrincipal xmldoc).
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Owner);
        var principal = CreateUserPrincipal(organizationClaims: "NorthWind-Labs");

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.ClaimMismatch, result.Error);
    }

    [Fact]
    public async Task Denies_when_the_target_organization_does_not_exist()
    {
        // Mandatory unknown-organization test: the token asserts the tenant and
        // the subject is known, but no organization row exists for the slug.
        var user = SeedUser();
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.OrganizationNotFound, result.Error);
    }

    [Fact]
    public async Task Denies_a_malformed_target_slug_as_not_found_without_throwing()
    {
        var principal = CreateUserPrincipal(organizationClaims: "not a slug!");

        var result = await _resolver.ResolveAsync(principal, "not a slug!", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.OrganizationNotFound, result.Error);
    }

    [Fact]
    public async Task Denies_when_the_subject_has_no_user_profile()
    {
        // Mandatory unknown-subject test: the token asserts the tenant and the
        // organization exists, but the OIDC identity has no persisted profile.
        SeedOrganization(_slugA);
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.UnknownSubject, result.Error);
    }

    [Fact]
    public async Task Denies_a_known_subject_with_no_membership_in_the_target_organization()
    {
        // Mandatory negative authorization test (threat T5): the token asserts
        // the tenant, the organization exists and the subject is known, but the
        // subject holds no membership in the target. Deny-by-default.
        SeedOrganization(_slugA);
        SeedUser();
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, result.Error);
    }

    [Fact]
    public async Task A_member_of_organization_A_is_denied_a_context_for_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5): the subject is a
        // member of organization A and the token even asserts BOTH tenants, but
        // the subject has no membership in organization B. Membership in A grants
        // nothing in B; the resolver must deny B with NoMembership and must never
        // hand back A's context for a B request.
        var organizationA = SeedOrganization(_slugA);
        SeedOrganization(_slugB);
        var user = SeedUser();
        SeedMembership(organizationA.Id, user.Id, MembershipRole.Owner);
        // Even a broad token that claims both tenants cannot manufacture a
        // membership in B.
        var principal = CreateUserPrincipal(organizationClaims: [_slugA, _slugB]);

        var resultForB = await _resolver.ResolveAsync(principal, _slugB, CancellationToken.None);
        var resultForA = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(resultForB.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, resultForB.Error);

        // The legitimate tenant A still resolves, and to A only.
        Assert.True(resultForA.Succeeded);
        Assert.Equal(organizationA.Id, resultForA.Context!.OrganizationId);
        Assert.Equal(_slugA, resultForA.Context.OrganizationSlug);
    }

    [Fact]
    public async Task The_resolved_context_carries_the_role_from_the_target_tenant_only()
    {
        // Tenant isolation of the role (threat T5): the subject is Owner in A
        // and Participant in B; the token asserts both. Each resolution must
        // carry exactly that tenant's role, never the other tenant's.
        var organizationA = SeedOrganization(_slugA);
        var organizationB = SeedOrganization(_slugB);
        var user = SeedUser();
        SeedMembership(organizationA.Id, user.Id, MembershipRole.Owner);
        SeedMembership(organizationB.Id, user.Id, MembershipRole.Participant);
        var principal = CreateUserPrincipal(organizationClaims: [_slugA, _slugB]);

        var resultForA = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);
        var resultForB = await _resolver.ResolveAsync(principal, _slugB, CancellationToken.None);

        Assert.True(resultForA.Succeeded);
        Assert.True(resultForB.Succeeded);
        Assert.Equal(MembershipRole.Owner, resultForA.Context!.Role);
        Assert.Equal(MembershipRole.Participant, resultForB.Context!.Role);
        // The owner role in A never leaks into B's context.
        Assert.False(resultForB.Context.HasRole(MembershipRole.Owner));
    }

    [Fact]
    public async Task A_different_subject_in_the_target_tenant_is_not_resolved_for_this_principal()
    {
        // The membership belongs to another subject in the same organization;
        // this principal (same issuer, different subject) is not a member and is
        // denied (threat T5: object-level, not just role-level, isolation).
        var organization = SeedOrganization(_slugA);
        var otherUser = SeedUser(subjectId: _otherSubject);
        SeedUser(); // this principal's profile exists, but holds no membership
        SeedMembership(organization.Id, otherUser.Id, MembershipRole.Owner);
        var principal = CreateUserPrincipal(organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(principal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, result.Error);
    }

    [Fact]
    public async Task The_same_oidc_subject_under_a_foreign_issuer_is_a_different_subject()
    {
        // The subject is identified by the full OIDC identity pair. A profile
        // and membership exist for (issuer, subject); a principal with the same
        // subject value under a FOREIGN issuer is a different user, has no
        // profile, and is denied as an unknown subject (threat T5).
        var organization = SeedOrganization(_slugA);
        var user = SeedUser();
        SeedMembership(organization.Id, user.Id, MembershipRole.Owner);
        var foreignIssuerPrincipal = CreateUserPrincipal(issuer: _foreignIssuer, organizationClaims: _slugA);

        var result = await _resolver.ResolveAsync(foreignIssuerPrincipal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.UnknownSubject, result.Error);
    }

    [Fact]
    public async Task Resolving_a_null_principal_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _resolver.ResolveAsync(null!, _slugA, CancellationToken.None));
    }

    private sealed class FakeOrganizationRepository : IOrganizationRepository
    {
        public List<Organization> Organizations { get; } = [];

        public int FindBySlugCallCount { get; private set; }

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Organization id must not be empty.", nameof(id));
            }

            return Task.FromResult(Organizations.FirstOrDefault(organization => organization.HasId(id)));
        }

        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
        {
            FindBySlugCallCount++;
            var canonicalSlug = Organization.CanonicalizeSlug(slug);
            return Task.FromResult(Organizations.FirstOrDefault(organization => organization.HasSlug(canonicalSlug)));
        }

        public Task<IReadOnlyList<Organization>> ListByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The tenant context resolver does not list organizations.");

        public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The tenant context resolver does not list memberships.");

        public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(organization);
            if (Organizations.Any(existing => existing.HasSlug(organization.Slug)))
            {
                return Task.FromResult(OrganizationAddResult.DuplicateSlug);
            }

            Organizations.Add(organization);
            return Task.FromResult(OrganizationAddResult.Added);
        }

        public Task<OrganizationAddResult> AddWithOwnerAsync(
            Organization organization,
            OrganizationMember owner,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The tenant context resolver does not create organizations.");
    }

    private sealed class FakeUserProfileRepository : IUserProfileRepository
    {
        public List<UserProfile> Profiles { get; } = [];

        public Task<UserProfile?> FindByOidcIdentityAsync(
            string issuer,
            string subjectId,
            CancellationToken cancellationToken)
        {
            if (!OidcPrincipal.IsValidIssuer(issuer))
            {
                throw new ArgumentException("Issuer violates the issuer invariants.", nameof(issuer));
            }

            if (!OidcPrincipal.IsValidSubjectId(subjectId))
            {
                throw new ArgumentException("Subject violates the subject invariants.", nameof(subjectId));
            }

            return Task.FromResult(
                Profiles.FirstOrDefault(profile => profile.BelongsToOidcIdentity(issuer, subjectId)));
        }

        public Task<UserProfileAddResult> AddAsync(UserProfile profile, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(profile);
            Profiles.Add(profile);
            return Task.FromResult(UserProfileAddResult.Added);
        }

        public Task UpdateAsync(UserProfile profile, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeOrganizationMemberRepository : IOrganizationMemberRepository
    {
        public List<OrganizationMember> Members { get; } = [];

        public int FindCallCount { get; private set; }

        public Task<OrganizationMember?> FindAsync(
            Guid organizationId,
            Guid userProfileId,
            CancellationToken cancellationToken)
        {
            FindCallCount++;
            if (organizationId == Guid.Empty)
            {
                throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
            }

            if (userProfileId == Guid.Empty)
            {
                throw new ArgumentException("Subject (user profile) id must not be empty.", nameof(userProfileId));
            }

            return Task.FromResult(
                Members.FirstOrDefault(member => member.Identifies(organizationId, userProfileId)));
        }

        public async Task<bool> IsMemberAsync(
            Guid organizationId,
            Guid userProfileId,
            CancellationToken cancellationToken)
        {
            var member = await FindAsync(organizationId, userProfileId, cancellationToken).ConfigureAwait(false);
            return member is not null;
        }

        public Task<OrganizationMemberAddResult> AddAsync(
            OrganizationMember member,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (Members.Any(existing => existing.Identifies(member.OrganizationId, member.UserProfileId)))
            {
                return Task.FromResult(OrganizationMemberAddResult.DuplicateMembership);
            }

            Members.Add(member);
            return Task.FromResult(OrganizationMemberAddResult.Added);
        }

        public Task<OrganizationMember?> FindByIdAsync(
            Guid organizationId,
            Guid memberId,
            CancellationToken cancellationToken)
        {
            if (organizationId == Guid.Empty)
            {
                throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
            }

            if (memberId == Guid.Empty)
            {
                throw new ArgumentException("Member id must not be empty.", nameof(memberId));
            }

            return Task.FromResult(
                Members.FirstOrDefault(member => member.Id == memberId && member.OrganizationId == organizationId));
        }

        public Task<int> CountByRoleAsync(
            Guid organizationId,
            MembershipRole role,
            CancellationToken cancellationToken)
        {
            if (organizationId == Guid.Empty)
            {
                throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
            }

            if (!OrganizationMember.IsValidRole(role))
            {
                throw new ArgumentOutOfRangeException(nameof(role), role, "Role is not a defined membership role.");
            }

            return Task.FromResult(
                Members.Count(member => member.OrganizationId == organizationId && member.HasRole(role)));
        }

        public Task RemoveAsync(OrganizationMember member, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(member);
            Members.RemoveAll(existing => existing.Id == member.Id);
            return Task.CompletedTask;
        }
    }
}
