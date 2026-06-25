// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Unit tests for <see cref="ClaimedOrganizationProvisioningService"/>
/// (CORE-ID-007), the first-login founding-owner counterpart of the
/// <see cref="TenantContextResolver"/>: it provisions the organization a verified
/// token organization claim names but that does not yet exist, founding the
/// caller as its Owner.
///
/// The tests drive the decision logic through an in-memory fake of the
/// organization repository, so every rule is exercised without a database. The
/// NEGATIVE cases below are mandatory (AGENTS.md; docs/17_DEFINITION_OF_DONE.md;
/// threats T5/T1 in docs/07_SECURITY_THREAT_MODEL.md): a claim naming an
/// already-existing organization provisions NOTHING (no auto-enrolment — an
/// existing tenant can never be joined from a claim), a non-canonical or invalid
/// claim provisions nothing, a service-account principal provisions nothing, and
/// a lost create race enrols the caller in nothing (fail-closed).
/// </summary>
public sealed class ClaimedOrganizationProvisioningServiceTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";
    private const string _slugA = "northwind-labs";
    private const string _slugB = "acme-co";

    private static readonly DateTimeOffset _now = new(2026, 6, 26, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeOrganizationRepository _organizations = new();
    private readonly ClaimedOrganizationProvisioningService _service;
    private readonly Guid _founderId = Guid.CreateVersion7();

    public ClaimedOrganizationProvisioningServiceTests()
    {
        _service = new ClaimedOrganizationProvisioningService(
            _organizations,
            new FakeTimeProvider(_now));
    }

    private static OidcPrincipal CreateUserPrincipal(params string[] organizationClaims)
        => new(PrincipalType.User, _issuer, _subject, organizationClaims: organizationClaims);

    private Organization SeedOrganization(string slug)
    {
        var organization = Organization.Create(slug, slug, _now);
        _organizations.Organizations.Add(organization);
        return organization;
    }

    // ---- positive: a claim for a not-yet-existing tenant founds it ----------

    [Fact]
    public async Task Provisions_the_claimed_tenant_and_founds_the_caller_as_owner()
    {
        var principal = CreateUserPrincipal(_slugA);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(1, provisioned);

        // The tenant was created with the claim as its canonical slug.
        var organization = Assert.Single(_organizations.Organizations);
        Assert.Equal(_slugA, organization.Slug);

        // The caller is the founding OWNER, referencing their own resolved profile.
        var owner = Assert.Single(_organizations.OwnersAdded);
        Assert.True(owner.BelongsToOrganization(organization.Id));
        Assert.True(owner.BelongsToSubject(_founderId));
        Assert.Equal(MembershipRole.Owner, owner.Role);
    }

    [Fact]
    public async Task Provisions_only_the_not_yet_existing_claimed_tenants()
    {
        // The token claims two tenants; A already exists, B does not. Only B is
        // founded; A is never auto-joined (the caller is not enrolled in it).
        SeedOrganization(_slugA);
        var principal = CreateUserPrincipal(_slugA, _slugB);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(1, provisioned);
        Assert.Contains(_organizations.Organizations, organization => organization.HasSlug(_slugB));
        var owner = Assert.Single(_organizations.OwnersAdded);
        var founded = Assert.Single(_organizations.Organizations, organization => organization.HasSlug(_slugB));
        Assert.True(owner.BelongsToOrganization(founded.Id));
    }

    // ---- idempotency --------------------------------------------------------

    [Fact]
    public async Task A_second_provision_of_the_same_claim_founds_nothing_more()
    {
        var principal = CreateUserPrincipal(_slugA);

        var first = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);
        var second = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_organizations.Organizations);
        Assert.Single(_organizations.OwnersAdded);
    }

    // ---- NEGATIVE: an existing tenant is never joined from a claim (T5/T1) ---

    [Fact]
    public async Task An_already_existing_claimed_tenant_provisions_nothing()
    {
        SeedOrganization(_slugA);
        var principal = CreateUserPrincipal(_slugA);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(0, provisioned);
        // No membership was added: the caller is never auto-enrolled in a pre-existing tenant.
        Assert.Empty(_organizations.OwnersAdded);
    }

    // ---- NEGATIVE: a lost create race enrols the caller in nothing ----------

    [Fact]
    public async Task A_lost_create_race_provisions_nothing_and_enrols_no_membership()
    {
        // The existence check sees no tenant, but the atomic create loses the race to a concurrent first-login
        // (the unique slug index rejects it). The founding membership rolls back with it, so the caller is NOT
        // enrolled in the tenant the other caller founded — fail-closed.
        _organizations.ForceDuplicateOnAdd = true;
        var principal = CreateUserPrincipal(_slugA);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(0, provisioned);
        Assert.Empty(_organizations.Organizations);
        Assert.Empty(_organizations.OwnersAdded);
    }

    // ---- NEGATIVE: a non-canonical / invalid claim provisions nothing -------

    [Theory]
    [InlineData("Northwind-Labs")] // wrong casing — would not match the claim exactly
    [InlineData("northwind labs")] // whitespace — not a slug shape
    [InlineData("a")]              // too short
    [InlineData("-bad-")]          // leading/trailing dash
    public async Task A_claim_that_is_not_a_canonical_slug_provisions_nothing(string claim)
    {
        // The claim value reaches the principal as raw token data; only a value that is ALREADY a canonical slug
        // names a tenant the founder could resolve (the resolver/me intersection match the claim exactly). A
        // non-canonical claim is skipped, never coerced into an unreachable org.
        var principal = CreateUserPrincipal(claim);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(0, provisioned);
        Assert.Empty(_organizations.Organizations);
        Assert.Empty(_organizations.OwnersAdded);
    }

    // ---- NEGATIVE: no claim, and a service account, provision nothing -------

    [Fact]
    public async Task A_principal_with_no_organization_claim_provisions_nothing()
    {
        var principal = CreateUserPrincipal();

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(0, provisioned);
        Assert.Empty(_organizations.Organizations);
    }

    [Fact]
    public async Task A_service_account_principal_provisions_nothing()
    {
        // Only a human user founds a tenant; a service account holds no user profile or membership.
        var principal = new OidcPrincipal(
            PrincipalType.ServiceAccount,
            _issuer,
            _subject,
            organizationClaims: [_slugA]);

        var provisioned = await _service.ProvisionClaimedTenantsAsync(principal, _founderId, CancellationToken.None);

        Assert.Equal(0, provisioned);
        Assert.Empty(_organizations.Organizations);
        Assert.Empty(_organizations.OwnersAdded);
    }

    // ---- input guards -------------------------------------------------------

    [Fact]
    public async Task A_null_principal_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _service.ProvisionClaimedTenantsAsync(null!, _founderId, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_founder_id_throws()
    {
        var principal = CreateUserPrincipal(_slugA);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ProvisionClaimedTenantsAsync(principal, Guid.Empty, CancellationToken.None));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeOrganizationRepository : IOrganizationRepository
    {
        public List<Organization> Organizations { get; } = [];

        public List<OrganizationMember> OwnersAdded { get; } = [];

        /// <summary>
        /// When set, <see cref="AddWithOwnerAsync"/> reports a duplicate slug WITHOUT persisting, simulating a
        /// lost create race (a concurrent first-login founded the tenant between the existence check and the
        /// atomic create, so the unique index rejects the insert and the membership rolls back with it).
        /// </summary>
        public bool ForceDuplicateOnAdd { get; set; }

        public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
        {
            var canonicalSlug = Organization.CanonicalizeSlug(slug);
            return Task.FromResult(Organizations.FirstOrDefault(organization => organization.HasSlug(canonicalSlug)));
        }

        public Task<OrganizationAddResult> AddWithOwnerAsync(
            Organization organization,
            OrganizationMember owner,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(organization);
            ArgumentNullException.ThrowIfNull(owner);

            // Mirror the real repository's invariant: the founding membership must belong to the tenant created.
            if (!owner.BelongsToOrganization(organization.Id))
            {
                throw new ArgumentException(
                    "The owner membership must belong to the organization being created.",
                    nameof(owner));
            }

            if (ForceDuplicateOnAdd || Organizations.Any(existing => existing.HasSlug(organization.Slug)))
            {
                // The unique slug index rejected the insert; the membership rolls back with it (nothing persists).
                return Task.FromResult(OrganizationAddResult.DuplicateSlug);
            }

            Organizations.Add(organization);
            OwnersAdded.Add(owner);
            return Task.FromResult(OrganizationAddResult.Added);
        }

        public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException("The provisioning service does not look up organizations by id.");

        public Task<IReadOnlyList<Organization>> ListByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The provisioning service does not list organizations.");

        public Task<IReadOnlyList<OrganizationMembershipView>> ListMembershipsByMemberAsync(
            Guid userProfileId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("The provisioning service does not list memberships.");

        public Task<OrganizationAddResult> AddAsync(Organization organization, CancellationToken cancellationToken)
            => throw new NotSupportedException("The provisioning service uses only the founding-owner create path.");

        public Task DeleteAsync(Organization organization, CancellationToken cancellationToken)
            => throw new NotSupportedException("The provisioning service does not delete organizations.");
    }
}
