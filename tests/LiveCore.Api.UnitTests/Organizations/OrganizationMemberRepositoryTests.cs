// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Integration-style tests for the EF Core-backed
/// <see cref="OrganizationMemberRepository"/> (CORE-ID-004).
///
/// They run against an in-memory SQLite database so the real model mapping,
/// SQL translation, the foreign keys into <c>organizations</c>/<c>users</c>
/// and the unique (<c>organization_id</c>, <c>user_id</c>) index are exercised
/// on every test run without any database server or Docker. The behaviors
/// under test (tenant-scoped equality lookups, unique index enforcement, full
/// isolation between tenants, the role floor) are relational semantics shared
/// with PostgreSQL; provider-specific verification happens against PostgreSQL
/// in the deployment pipeline (livecore-deploy) and the isolation test story
/// CORE-ID-006.
///
/// This is the first authorization-relevant relationship in Core, so the
/// negative cases below are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md;
/// threat T5 in docs/07_SECURITY_THREAT_MODEL.md): a subject who is a member of
/// organization A is not a member of organization B; a cross-tenant lookup
/// returns null/deny; a subject with no membership in a tenant is denied; a
/// duplicate (organization, subject) is rejected leaving the existing row
/// unchanged.
/// </summary>
public sealed class OrganizationMemberRepositoryTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _otherSubject = "11111111-2222-3333-4444-555555555555";
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public OrganizationMemberRepositoryTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads
        // genuinely round-trip through the database.
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // SQLite does not enforce foreign keys unless asked; turn enforcement
        // on so the FK constraints in the model are genuinely exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationRepository(context);
        Assert.Equal(OrganizationAddResult.Added, await repository.AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<UserProfile> SeedUserAsync(string issuer, string subjectId)
    {
        var profile = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, issuer, subjectId),
            _createdAt);
        await using var context = CreateContext();
        var repository = new UserProfileRepository(context);
        Assert.Equal(UserProfileAddResult.Added, await repository.AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<OrganizationMember> SeedMembershipAsync(
        Guid organizationId,
        Guid userProfileId,
        MembershipRole role)
    {
        var member = OrganizationMember.Create(organizationId, userProfileId, role, _createdAt);
        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);
        Assert.Equal(
            OrganizationMemberAddResult.Added,
            await repository.AddAsync(member, CancellationToken.None));
        return member;
    }

    [Fact]
    public async Task Membership_round_trips_through_the_database()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var seeded = await SeedMembershipAsync(organization.Id, user.Id, MembershipRole.Admin);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);
        var loaded = await repository.FindAsync(organization.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(seeded.Id, loaded.Id);
        Assert.Equal(organization.Id, loaded.OrganizationId);
        Assert.Equal(user.Id, loaded.UserProfileId);
        Assert.Equal(MembershipRole.Admin, loaded.Role);
        Assert.Equal(seeded.CreatedAt, loaded.CreatedAt);
        Assert.Equal(seeded.UpdatedAt, loaded.UpdatedAt);
    }

    [Fact]
    public async Task Lookup_for_a_subject_without_a_membership_returns_null()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);
        var loaded = await repository.FindAsync(organization.Id, user.Id, CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Lookup_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.FindAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Membership_in_organization_A_grants_nothing_in_organization_B()
    {
        // Mandatory negative foreign-tenant test (threat T5): the subject is a
        // member of organization A only. Looking the subject up in
        // organization B must return null even though both the subject and
        // organization B exist; membership in A grants nothing in B.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organizationA.Id, user.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        var inA = await repository.FindAsync(organizationA.Id, user.Id, CancellationToken.None);
        var inB = await repository.FindAsync(organizationB.Id, user.Id, CancellationToken.None);

        Assert.NotNull(inA);
        Assert.Equal(organizationA.Id, inA.OrganizationId);
        Assert.Null(inB);
    }

    [Fact]
    public async Task IsMember_denies_a_subject_in_a_foreign_organization()
    {
        // Mandatory negative authorization test (threat T5): an owner in
        // organization A is denied any standing in organization B. Membership
        // in one tenant is never visible through another tenant's id.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        var user = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organizationA.Id, user.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        Assert.True(await repository.IsMemberAsync(
            organizationA.Id, user.Id, CancellationToken.None));
        Assert.False(await repository.IsMemberAsync(
            organizationB.Id, user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsMember_denies_a_subject_with_no_membership()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        // Deny-by-default: a user that exists but holds no membership in the
        // tenant is not a member.
        Assert.False(await repository.IsMemberAsync(
            organization.Id, user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsMember_rejects_empty_ids()
    {
        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.IsMemberAsync(Guid.Empty, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => repository.IsMemberAsync(Guid.CreateVersion7(), Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Duplicate_membership_is_rejected_and_the_existing_row_stays_unchanged()
    {
        // Mandatory uniqueness test: the unique (organization_id, user_id)
        // index is the database-level guarantee that a second writer can never
        // create a conflicting standing for the same subject in the same
        // tenant (threat T5).
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var existing = await SeedMembershipAsync(organization.Id, user.Id, MembershipRole.Participant);
        var duplicate = OrganizationMember.Create(organization.Id, user.Id, MembershipRole.Owner, _updatedAt);

        await using (var context = CreateContext())
        {
            var repository = new OrganizationMemberRepository(context);
            var result = await repository.AddAsync(duplicate, CancellationToken.None);

            Assert.Equal(OrganizationMemberAddResult.DuplicateMembership, result);
        }

        await using (var context = CreateContext())
        {
            var repository = new OrganizationMemberRepository(context);
            var loaded = await repository.FindAsync(organization.Id, user.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            // The original row (its id and its role) is untouched; the
            // duplicate's higher role never overwrote the existing standing.
            Assert.Equal(existing.Id, loaded.Id);
            Assert.Equal(MembershipRole.Participant, loaded.Role);
        }
    }

    [Fact]
    public async Task The_same_subject_can_be_a_member_of_two_organizations_with_independent_roles()
    {
        // Tenant isolation works in both directions: one subject may hold a
        // membership in each of two organizations, and each membership is
        // scoped to and resolved only within its own organization.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);
        var user = await SeedUserAsync(_issuer, _subject);
        var inA = await SeedMembershipAsync(organizationA.Id, user.Id, MembershipRole.Owner);
        var inB = await SeedMembershipAsync(organizationB.Id, user.Id, MembershipRole.Participant);

        Assert.NotEqual(inA.Id, inB.Id);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        var loadedA = await repository.FindAsync(organizationA.Id, user.Id, CancellationToken.None);
        var loadedB = await repository.FindAsync(organizationB.Id, user.Id, CancellationToken.None);

        Assert.NotNull(loadedA);
        Assert.NotNull(loadedB);
        Assert.Equal(MembershipRole.Owner, loadedA.Role);
        Assert.Equal(MembershipRole.Participant, loadedB.Role);
        // The owner role in A never leaks into B: the subject's standing in B
        // is exactly Participant, nothing more (threat T5).
        Assert.True(loadedB.HasRole(MembershipRole.Participant));
        Assert.False(loadedB.HasRole(MembershipRole.Owner));
    }

    [Fact]
    public async Task Two_subjects_in_one_organization_are_addressed_independently()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var otherUser = await SeedUserAsync(_issuer, _otherSubject);
        await SeedMembershipAsync(organization.Id, user.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organization.Id, otherUser.Id, MembershipRole.Observer);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        var first = await repository.FindAsync(organization.Id, user.Id, CancellationToken.None);
        var second = await repository.FindAsync(organization.Id, otherUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MembershipRole.Owner, first.Role);
        Assert.Equal(MembershipRole.Observer, second.Role);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Same_oidc_subject_under_two_issuers_are_distinct_members()
    {
        // The subject is identified by the user profile surrogate id, which is
        // keyed by the OIDC identity pair (issuer, subject). The same OIDC
        // subject value under two issuers is two distinct users and therefore
        // two distinct memberships, even within one organization (threat T5).
        var organization = await SeedOrganizationAsync(_slugA);
        var user = await SeedUserAsync(_issuer, _subject);
        var foreignIssuerUser = await SeedUserAsync(_foreignIssuer, _subject);

        Assert.NotEqual(user.Id, foreignIssuerUser.Id);

        await SeedMembershipAsync(organization.Id, user.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organization.Id, foreignIssuerUser.Id, MembershipRole.Auditor);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        var first = await repository.FindAsync(organization.Id, user.Id, CancellationToken.None);
        var second = await repository.FindAsync(organization.Id, foreignIssuerUser.Id, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(MembershipRole.Owner, first.Role);
        Assert.Equal(MembershipRole.Auditor, second.Role);
    }

    [Fact]
    public async Task IsSoleOwnerOfAnyOrganizationAsync_is_true_only_when_a_tenant_has_no_other_owner()
    {
        // CORE-PRIV-001 orphan guard: a subject who is the sole Owner of a tenant cannot be erased.
        var organization = await SeedOrganizationAsync(_slugA);
        var soleOwner = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organization.Id, soleOwner.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        Assert.True(await repository.IsSoleOwnerOfAnyOrganizationAsync(soleOwner.Id, CancellationToken.None));

        // Once a second Owner joins, the first is no longer the sole Owner.
        var coOwner = await SeedUserAsync(_issuer, _otherSubject);
        await SeedMembershipAsync(organization.Id, coOwner.Id, MembershipRole.Owner);
        Assert.False(await repository.IsSoleOwnerOfAnyOrganizationAsync(soleOwner.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsSoleOwnerOfAnyOrganizationAsync_is_false_for_a_non_owner_member()
    {
        var organization = await SeedOrganizationAsync(_slugA);
        var owner = await SeedUserAsync(_issuer, _subject);
        await SeedMembershipAsync(organization.Id, owner.Id, MembershipRole.Owner);
        var participant = await SeedUserAsync(_issuer, _otherSubject);
        await SeedMembershipAsync(organization.Id, participant.Id, MembershipRole.Participant);

        await using var context = CreateContext();
        var repository = new OrganizationMemberRepository(context);

        // A Participant owns nothing, so erasing them orphans no tenant.
        Assert.False(await repository.IsSoleOwnerOfAnyOrganizationAsync(participant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task IsSoleOwnerOfAnyOrganizationAsync_rejects_an_empty_subject_id()
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            new OrganizationMemberRepository(CreateContext())
                .IsSoleOwnerOfAnyOrganizationAsync(Guid.Empty, CancellationToken.None));
}
