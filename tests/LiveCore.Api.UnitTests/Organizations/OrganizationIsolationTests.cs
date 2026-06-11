using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Cross-cutting organization isolation suite for the Identity and Tenant
/// Boundaries epic (CORE-ID-006). This is the dedicated tenant-isolation story
/// for threat T5 ("data from organization A is visible to organization B",
/// docs/07_SECURITY_THREAT_MODEL.md): it proves, end-to-end, that organization
/// A's data is never reachable from organization B across the Identity
/// aggregates built so far.
///
/// Unlike the per-aggregate tests (which exercise one repository or the
/// resolver against in-memory fakes), every test here drives the REAL
/// collaborators together over ONE shared in-memory SQLite database with
/// foreign keys enforced: the <see cref="UserProfileRepository"/>, the
/// <see cref="OrganizationRepository"/>, the
/// <see cref="OrganizationMemberRepository"/>, the
/// <see cref="LiveCoreDbContext"/> model mapping (unique indexes, FKs) and the
/// <see cref="TenantContextResolver"/> chokepoint. Two fully populated tenants
/// (organization A and organization B), each with their own organization, users
/// and memberships, live side by side in that single database, exactly like a
/// real multi-tenant deployment.
///
/// The SQLite behaviors under test (tenant-scoped equality lookups, unique
/// index enforcement, case-sensitive identity/slug comparison, full isolation
/// between tenants and through the resolver) are relational semantics shared
/// with PostgreSQL; provider-specific verification against PostgreSQL happens in
/// the deployment pipeline (livecore-deploy). The negative foreign-tenant cases
/// below are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md; threat T5).
/// </summary>
public sealed class OrganizationIsolationTests : IDisposable
{
    // Two issuers: the same OIDC subject value under two issuers is two
    // different users and must never be conflated (threat T5).
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _foreignIssuer = "https://id.foreign.test/realms/livecore";

    // Distinct subjects. _sharedSubject deliberately reuses one subject VALUE
    // across both issuers to prove the (issuer, subject) pair is the identity.
    private const string _subjectAlice = "11111111-1111-1111-1111-111111111111";
    private const string _subjectBob = "22222222-2222-2222-2222-222222222222";
    private const string _subjectCarol = "33333333-3333-3333-3333-333333333333";
    private const string _sharedSubject = "99999999-9999-9999-9999-999999999999";

    // Two tenants in one database.
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public OrganizationIsolationTests()
    {
        // One open connection per test keeps the private in-memory database
        // alive while every step still uses its own context, so reads
        // genuinely round-trip through the database (matching the per-aggregate
        // repository tests).
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

    private TenantContextResolver CreateResolver(LiveCoreDbContext context)
        => new(
            new OrganizationRepository(context),
            new UserProfileRepository(context),
            new OrganizationMemberRepository(context));

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

    private static OidcPrincipal UserPrincipal(
        string issuer,
        string subjectId,
        params string[] organizationClaims)
        => new(PrincipalType.User, issuer, subjectId, organizationClaims: organizationClaims);

    /// <summary>
    /// Seeds two fully populated tenants in the one shared database:
    /// <list type="bullet">
    ///   <item>organization A with Alice (Owner) and Bob (Participant);</item>
    ///   <item>organization B with Carol (Owner) and Bob (Auditor) — Bob is a
    ///   member of BOTH tenants with independent roles.</item>
    /// </list>
    /// Returns the seeded fixture so each test can assert isolation against it.
    /// </summary>
    private async Task<Fixture> SeedTwoTenantsAsync()
    {
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);

        var alice = await SeedUserAsync(_issuer, _subjectAlice);
        var bob = await SeedUserAsync(_issuer, _subjectBob);
        var carol = await SeedUserAsync(_issuer, _subjectCarol);

        await SeedMembershipAsync(organizationA.Id, alice.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organizationA.Id, bob.Id, MembershipRole.Participant);
        await SeedMembershipAsync(organizationB.Id, carol.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organizationB.Id, bob.Id, MembershipRole.Auditor);

        return new Fixture(organizationA, organizationB, alice, bob, carol);
    }

    private sealed record Fixture(
        Organization OrganizationA,
        Organization OrganizationB,
        UserProfile Alice,
        UserProfile Bob,
        UserProfile Carol);

    // ----------------------------------------------------------------------
    // End-to-end resolver isolation: the whole stack wired together.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task A_member_of_A_resolves_a_context_for_A_only()
    {
        // Alice is an Owner of A only. Resolving her for A succeeds end-to-end
        // through the real repositories + DbContext + resolver; resolving her
        // for B is denied as NoMembership even though B is a real tenant in the
        // same database (threat T5).
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var alice = UserPrincipal(_issuer, _subjectAlice, _slugA, _slugB);

        var forA = await resolver.ResolveAsync(alice, _slugA, CancellationToken.None);
        var forB = await resolver.ResolveAsync(alice, _slugB, CancellationToken.None);

        Assert.True(forA.Succeeded);
        Assert.Equal(fixture.OrganizationA.Id, forA.Context!.OrganizationId);
        Assert.Equal(_slugA, forA.Context.OrganizationSlug);
        Assert.Equal(fixture.Alice.Id, forA.Context.UserProfileId);
        Assert.Equal(MembershipRole.Owner, forA.Context.Role);

        // The broad token claiming both tenants cannot manufacture standing in
        // B: deny-by-default, and A's context is never returned for a B request.
        Assert.False(forB.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, forB.Error);
        Assert.Null(forB.Context);
    }

    [Fact]
    public async Task A_member_of_B_resolves_a_context_for_B_only()
    {
        // Symmetric direction: Carol is an Owner of B only and is denied A.
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var carol = UserPrincipal(_issuer, _subjectCarol, _slugA, _slugB);

        var forB = await resolver.ResolveAsync(carol, _slugB, CancellationToken.None);
        var forA = await resolver.ResolveAsync(carol, _slugA, CancellationToken.None);

        Assert.True(forB.Succeeded);
        Assert.Equal(fixture.OrganizationB.Id, forB.Context!.OrganizationId);
        Assert.Equal(fixture.Carol.Id, forB.Context.UserProfileId);
        Assert.Equal(MembershipRole.Owner, forB.Context.Role);

        Assert.False(forA.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, forA.Error);
    }

    [Fact]
    public async Task A_dual_tenant_member_resolves_the_role_of_each_target_tenant_only()
    {
        // Bob is a member of BOTH tenants with independent roles: Participant in
        // A and Auditor in B. Each resolution must carry exactly that tenant's
        // role; the role never leaks across the boundary (threat T5). The roles
        // are also not linearly ordered, so neither satisfies the other's
        // HasRole check (docs/06_AUTHORIZATION_MATRIX.md).
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var bob = UserPrincipal(_issuer, _subjectBob, _slugA, _slugB);

        var forA = await resolver.ResolveAsync(bob, _slugA, CancellationToken.None);
        var forB = await resolver.ResolveAsync(bob, _slugB, CancellationToken.None);

        Assert.True(forA.Succeeded);
        Assert.True(forB.Succeeded);

        // Same subject, but two different tenant ids and two different roles.
        Assert.Equal(fixture.OrganizationA.Id, forA.Context!.OrganizationId);
        Assert.Equal(fixture.OrganizationB.Id, forB.Context!.OrganizationId);
        Assert.Equal(fixture.Bob.Id, forA.Context.UserProfileId);
        Assert.Equal(fixture.Bob.Id, forB.Context.UserProfileId);

        Assert.Equal(MembershipRole.Participant, forA.Context.Role);
        Assert.Equal(MembershipRole.Auditor, forB.Context.Role);

        // The Participant role from A never leaks into B's context, and vice
        // versa.
        Assert.True(forA.Context.HasRole(MembershipRole.Participant));
        Assert.False(forA.Context.HasRole(MembershipRole.Auditor));
        Assert.True(forB.Context.HasRole(MembershipRole.Auditor));
        Assert.False(forB.Context.HasRole(MembershipRole.Participant));
    }

    [Fact]
    public async Task A_member_of_A_with_a_broad_token_claiming_B_is_still_denied_B()
    {
        // Defence in depth (threat T5): a token that claims B does not grant B
        // without a persisted membership in B. The resolver requires BOTH the
        // claim AND the membership, so the broad claim alone is denied.
        await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var alice = UserPrincipal(_issuer, _subjectAlice, _slugA, _slugB);

        var forB = await resolver.ResolveAsync(alice, _slugB, CancellationToken.None);

        Assert.False(forB.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, forB.Error);
    }

    [Fact]
    public async Task A_member_of_A_whose_token_does_not_claim_A_is_denied_before_the_database()
    {
        // The flip side of defence in depth: a real, entitled member of A whose
        // token does not assert A is denied on the CLAIM, before any tenant data
        // is touched (threat T5). A stale/forged membership without a matching
        // token claim cannot cross the boundary.
        await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        // Alice is an Owner of A, but her token claims only B.
        var alice = UserPrincipal(_issuer, _subjectAlice, _slugB);

        var forA = await resolver.ResolveAsync(alice, _slugA, CancellationToken.None);

        Assert.False(forA.Succeeded);
        Assert.Equal(TenantContextResolutionError.ClaimMismatch, forA.Error);
    }

    [Fact]
    public async Task A_subject_of_A_is_never_resolved_for_B_using_a_role_that_only_exists_in_A()
    {
        // Object-level isolation, not just role-level (threat T1/T5): Alice is
        // Owner in A. A B request for Alice must not resolve to B at all (she is
        // not a member of B), so no Owner standing can be reached in B through
        // her A membership.
        await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var alice = UserPrincipal(_issuer, _subjectAlice, _slugA, _slugB);

        var forB = await resolver.ResolveAsync(alice, _slugB, CancellationToken.None);

        Assert.False(forB.Succeeded);
        Assert.Null(forB.Context);
    }

    [Fact]
    public async Task A_subject_with_no_membership_in_either_tenant_is_denied_both()
    {
        // A known user with a profile but no membership anywhere is denied every
        // tenant: deny-by-default across the whole database.
        await SeedTwoTenantsAsync();
        var dave = await SeedUserAsync(_issuer, "44444444-4444-4444-4444-444444444444");
        Assert.NotEqual(Guid.Empty, dave.Id);

        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var davePrincipal = UserPrincipal(
            _issuer,
            "44444444-4444-4444-4444-444444444444",
            _slugA,
            _slugB);

        var forA = await resolver.ResolveAsync(davePrincipal, _slugA, CancellationToken.None);
        var forB = await resolver.ResolveAsync(davePrincipal, _slugB, CancellationToken.None);

        Assert.False(forA.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, forA.Error);
        Assert.False(forB.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, forB.Error);
    }

    // ----------------------------------------------------------------------
    // Identity isolation: the (issuer, subject) pair is the user, end-to-end.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task The_same_subject_value_under_two_issuers_are_two_users_with_independent_memberships()
    {
        // The same OIDC subject VALUE under two issuers is two different users.
        // Each is given a membership in a DIFFERENT tenant; resolving one must
        // never resolve the other's tenant or role (threat T5), proven through
        // the full resolver stack.
        var organizationA = await SeedOrganizationAsync(_slugA);
        var organizationB = await SeedOrganizationAsync(_slugB);

        var userUnderHomeIssuer = await SeedUserAsync(_issuer, _sharedSubject);
        var userUnderForeignIssuer = await SeedUserAsync(_foreignIssuer, _sharedSubject);
        Assert.NotEqual(userUnderHomeIssuer.Id, userUnderForeignIssuer.Id);

        await SeedMembershipAsync(organizationA.Id, userUnderHomeIssuer.Id, MembershipRole.Owner);
        await SeedMembershipAsync(organizationB.Id, userUnderForeignIssuer.Id, MembershipRole.Host);

        await using var context = CreateContext();
        var resolver = CreateResolver(context);

        // The home-issuer principal is entitled to A only.
        var homePrincipal = UserPrincipal(_issuer, _sharedSubject, _slugA, _slugB);
        var homeForA = await resolver.ResolveAsync(homePrincipal, _slugA, CancellationToken.None);
        var homeForB = await resolver.ResolveAsync(homePrincipal, _slugB, CancellationToken.None);

        Assert.True(homeForA.Succeeded);
        Assert.Equal(organizationA.Id, homeForA.Context!.OrganizationId);
        Assert.Equal(userUnderHomeIssuer.Id, homeForA.Context.UserProfileId);
        Assert.Equal(MembershipRole.Owner, homeForA.Context.Role);
        // The foreign-issuer user's B membership is NOT reachable for the
        // home-issuer principal, even though the subject value is identical.
        Assert.False(homeForB.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, homeForB.Error);

        // The foreign-issuer principal is entitled to B only, with its own role.
        var foreignPrincipal = UserPrincipal(_foreignIssuer, _sharedSubject, _slugA, _slugB);
        var foreignForB = await resolver.ResolveAsync(foreignPrincipal, _slugB, CancellationToken.None);
        var foreignForA = await resolver.ResolveAsync(foreignPrincipal, _slugA, CancellationToken.None);

        Assert.True(foreignForB.Succeeded);
        Assert.Equal(organizationB.Id, foreignForB.Context!.OrganizationId);
        Assert.Equal(userUnderForeignIssuer.Id, foreignForB.Context.UserProfileId);
        Assert.Equal(MembershipRole.Host, foreignForB.Context.Role);
        Assert.False(foreignForA.Succeeded);
        Assert.Equal(TenantContextResolutionError.NoMembership, foreignForA.Error);
    }

    [Fact]
    public async Task A_principal_under_a_foreign_issuer_is_an_unknown_subject_even_with_a_matching_membership()
    {
        // A profile and membership exist for (home issuer, subject) in A. A
        // principal with the SAME subject value under a FOREIGN issuer has no
        // profile, so it is denied as UnknownSubject before any membership is
        // consulted (threat T5).
        var organizationA = await SeedOrganizationAsync(_slugA);
        var homeUser = await SeedUserAsync(_issuer, _sharedSubject);
        await SeedMembershipAsync(organizationA.Id, homeUser.Id, MembershipRole.Owner);

        await using var context = CreateContext();
        var resolver = CreateResolver(context);
        var foreignPrincipal = UserPrincipal(_foreignIssuer, _sharedSubject, _slugA);

        var result = await resolver.ResolveAsync(foreignPrincipal, _slugA, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(TenantContextResolutionError.UnknownSubject, result.Error);
    }

    // ----------------------------------------------------------------------
    // Read-path isolation through the real repositories on the shared database.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Every_membership_read_path_is_tenant_scoped_across_the_shared_database()
    {
        // With both tenants populated, every (organization, subject) lookup
        // returns only the membership for exactly that pair. Cross-tenant
        // lookups (a tenant's member queried under the other tenant's id) return
        // null/deny, in both directions (threat T5).
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var members = new OrganizationMemberRepository(context);

        // Alice (A-only): found in A, absent in B.
        Assert.NotNull(await members.FindAsync(fixture.OrganizationA.Id, fixture.Alice.Id, CancellationToken.None));
        Assert.Null(await members.FindAsync(fixture.OrganizationB.Id, fixture.Alice.Id, CancellationToken.None));
        Assert.True(await members.IsMemberAsync(fixture.OrganizationA.Id, fixture.Alice.Id, CancellationToken.None));
        Assert.False(await members.IsMemberAsync(fixture.OrganizationB.Id, fixture.Alice.Id, CancellationToken.None));

        // Carol (B-only): found in B, absent in A.
        Assert.NotNull(await members.FindAsync(fixture.OrganizationB.Id, fixture.Carol.Id, CancellationToken.None));
        Assert.Null(await members.FindAsync(fixture.OrganizationA.Id, fixture.Carol.Id, CancellationToken.None));

        // Bob (both): each lookup returns exactly that tenant's row and role.
        var bobInA = await members.FindAsync(fixture.OrganizationA.Id, fixture.Bob.Id, CancellationToken.None);
        var bobInB = await members.FindAsync(fixture.OrganizationB.Id, fixture.Bob.Id, CancellationToken.None);
        Assert.NotNull(bobInA);
        Assert.NotNull(bobInB);
        Assert.NotEqual(bobInA.Id, bobInB.Id);
        Assert.Equal(MembershipRole.Participant, bobInA.Role);
        Assert.Equal(MembershipRole.Auditor, bobInB.Role);
    }

    [Fact]
    public async Task A_user_profile_is_only_addressable_by_its_full_oidc_identity_pair()
    {
        // The users table is global, but identity reads are exact on the full
        // (issuer, subject) pair: a near-match (foreign issuer, or another
        // tenant's subject value) never returns a profile (threat T5).
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var users = new UserProfileRepository(context);

        var alice = await users.FindByOidcIdentityAsync(_issuer, _subjectAlice, CancellationToken.None);
        Assert.NotNull(alice);
        Assert.Equal(fixture.Alice.Id, alice.Id);

        // Same subject value, foreign issuer: no profile.
        Assert.Null(await users.FindByOidcIdentityAsync(_foreignIssuer, _subjectAlice, CancellationToken.None));
        // Home issuer, but a subject value that belongs to nobody: no profile.
        Assert.Null(await users.FindByOidcIdentityAsync(
            _issuer, "00000000-0000-0000-0000-000000000000", CancellationToken.None));
    }

    [Fact]
    public async Task An_organization_is_only_addressable_by_its_own_id_or_slug()
    {
        // Each tenant's id and slug address only that tenant; neither resolves
        // the other (threat T5). No field of one tenant is reachable via the
        // other's identifier.
        var fixture = await SeedTwoTenantsAsync();
        await using var context = CreateContext();
        var organizations = new OrganizationRepository(context);

        var aById = await organizations.FindByIdAsync(fixture.OrganizationA.Id, CancellationToken.None);
        var bById = await organizations.FindByIdAsync(fixture.OrganizationB.Id, CancellationToken.None);
        var aBySlug = await organizations.FindBySlugAsync(_slugA, CancellationToken.None);
        var bBySlug = await organizations.FindBySlugAsync(_slugB, CancellationToken.None);

        Assert.NotNull(aById);
        Assert.NotNull(bById);
        Assert.NotNull(aBySlug);
        Assert.NotNull(bBySlug);

        Assert.Equal(fixture.OrganizationA.Id, aById.Id);
        Assert.Equal(fixture.OrganizationB.Id, bById.Id);
        Assert.Equal(fixture.OrganizationA.Id, aBySlug.Id);
        Assert.Equal(fixture.OrganizationB.Id, bBySlug.Id);

        // A's slug never returns B, and B's id never returns A.
        Assert.NotEqual(fixture.OrganizationB.Id, aBySlug.Id);
        Assert.NotEqual(fixture.OrganizationA.Id, bById.Id);
    }

    // ----------------------------------------------------------------------
    // Cross-tenant uniqueness boundaries on the shared database.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task The_same_slug_cannot_create_two_tenants()
    {
        // The unique slug index is the database-level tenant boundary: a second
        // organization with an existing slug is rejected, and the existing
        // tenant is untouched (threat T5).
        var organizationA = await SeedOrganizationAsync(_slugA);
        var duplicate = Organization.Create(_slugA, "A Different Display Name", _createdAt);

        await using var context = CreateContext();
        var organizations = new OrganizationRepository(context);
        var result = await organizations.AddAsync(duplicate, CancellationToken.None);

        Assert.Equal(OrganizationAddResult.DuplicateSlug, result);
        var loaded = await organizations.FindBySlugAsync(_slugA, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(organizationA.Id, loaded.Id);
        Assert.NotEqual(duplicate.Id, loaded.Id);
    }

    [Fact]
    public async Task A_subject_has_at_most_one_membership_per_tenant_but_one_per_each_tenant()
    {
        // The (organization_id, user_id) unique index scopes uniqueness to ONE
        // tenant. The same user may hold one membership in EACH tenant (Bob
        // already does in the fixture), but a second membership for the same
        // (tenant, user) pair is rejected (threat T5).
        var fixture = await SeedTwoTenantsAsync();

        // Bob legitimately has one membership in each tenant.
        await using (var context = CreateContext())
        {
            var members = new OrganizationMemberRepository(context);
            Assert.NotNull(await members.FindAsync(fixture.OrganizationA.Id, fixture.Bob.Id, CancellationToken.None));
            Assert.NotNull(await members.FindAsync(fixture.OrganizationB.Id, fixture.Bob.Id, CancellationToken.None));
        }

        // A second membership for (organization A, Bob) is a duplicate; the
        // existing Participant standing must survive untouched (a higher role
        // must not overwrite it).
        var duplicate = OrganizationMember.Create(
            fixture.OrganizationA.Id,
            fixture.Bob.Id,
            MembershipRole.Owner,
            _createdAt);
        await using (var context = CreateContext())
        {
            var members = new OrganizationMemberRepository(context);
            Assert.Equal(
                OrganizationMemberAddResult.DuplicateMembership,
                await members.AddAsync(duplicate, CancellationToken.None));
        }

        await using (var context = CreateContext())
        {
            var members = new OrganizationMemberRepository(context);
            var bobInA = await members.FindAsync(fixture.OrganizationA.Id, fixture.Bob.Id, CancellationToken.None);
            Assert.NotNull(bobInA);
            Assert.Equal(MembershipRole.Participant, bobInA.Role);
        }
    }

    [Fact]
    public async Task A_membership_cannot_reference_a_subject_that_does_not_exist()
    {
        // The user_id foreign key is enforced (PRAGMA foreign_keys = ON): a
        // membership in a real tenant for a non-existent subject is rejected, so
        // a dangling membership can never grant tenant standing (threat T5).
        var organizationA = await SeedOrganizationAsync(_slugA);
        var ghost = OrganizationMember.Create(
            organizationA.Id,
            Guid.CreateVersion7(),
            MembershipRole.Owner,
            _createdAt);

        await using var context = CreateContext();
        var members = new OrganizationMemberRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => members.AddAsync(ghost, CancellationToken.None));
    }

    [Fact]
    public async Task A_membership_cannot_reference_a_tenant_that_does_not_exist()
    {
        // The organization_id foreign key is enforced: a membership for a real
        // subject but a non-existent tenant is rejected (threat T5).
        var user = await SeedUserAsync(_issuer, _subjectAlice);
        var ghost = OrganizationMember.Create(
            Guid.CreateVersion7(),
            user.Id,
            MembershipRole.Owner,
            _createdAt);

        await using var context = CreateContext();
        var members = new OrganizationMemberRepository(context);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => members.AddAsync(ghost, CancellationToken.None));
    }

    // ----------------------------------------------------------------------
    // Case-sensitive identity/slug comparison at the database level.
    //
    // These assert the CURRENT behavior: the model does not yet pin a
    // deterministic column collation (deferred to this story in the
    // UserProfileConfiguration / OrganizationConfiguration xmldoc). SQLite's
    // default BINARY collation already makes equality exact and case-sensitive,
    // matching PostgreSQL's deterministic default ("C"/locale) collations. The
    // explicit column-collation pin remains a follow-up (see the test summary);
    // these tests lock in the case-sensitive behavior the boundary depends on so
    // a regression toward a case-insensitive collation would fail here.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task A_slug_that_differs_only_in_case_does_not_address_a_tenant()
    {
        // Insert an upper-case slug directly (bypassing domain canonicalization)
        // so the DATABASE comparison itself is exercised. A canonical lookup for
        // the lower-case slug must NOT return the upper-case row: a future bypass
        // of canonicalization can never let one tenant be reached through
        // another's slug in a different case (threat T5).
        await using (var seedContext = CreateContext())
        {
            await seedContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO organizations (id, slug, name, created_at, updated_at) VALUES ({0}, {1}, {2}, {3}, {3})",
                Guid.CreateVersion7().ToString(),
                _slugA.ToUpperInvariant(),
                "Upper Case Tenant",
                _createdAt.UtcDateTime.ToString("o"));
        }

        await using var context = CreateContext();
        var organizations = new OrganizationRepository(context);

        Assert.Null(await organizations.FindBySlugAsync(_slugA, CancellationToken.None));
    }

    [Fact]
    public async Task An_identity_pair_that_differs_only_in_case_does_not_address_a_user()
    {
        // Case variance on either identity value must never address a foreign
        // profile (threat T5). The stored values are exact; an upper-cased
        // issuer or subject returns nothing. The subject below intentionally
        // contains lower-case hex letters so upper-casing genuinely changes it
        // (an all-digit subject would be unchanged by ToUpperInvariant and would
        // not exercise the comparison).
        const string letteredSubject = "abcdef01-2345-6789-abcd-ef0123456789";
        await SeedUserAsync(_issuer, letteredSubject);
        Assert.NotEqual(letteredSubject, letteredSubject.ToUpperInvariant());

        await using var context = CreateContext();
        var users = new UserProfileRepository(context);

        // Exact pair is found.
        Assert.NotNull(await users.FindByOidcIdentityAsync(_issuer, letteredSubject, CancellationToken.None));

        // Upper-cased issuer or subject must not address the stored profile.
        Assert.Null(await users.FindByOidcIdentityAsync(
            _issuer.ToUpperInvariant(), letteredSubject, CancellationToken.None));
        Assert.Null(await users.FindByOidcIdentityAsync(
            _issuer, letteredSubject.ToUpperInvariant(), CancellationToken.None));
    }
}
