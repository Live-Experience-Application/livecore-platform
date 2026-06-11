using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Unit tests for the <see cref="TenantContext"/> value type (CORE-ID-005):
/// the internal mint guard refuses to build a context from a membership that
/// does not belong to exactly the resolved organization and subject (threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md), and the role accessor exposes only an
/// exact-role check (the roles are not linearly ordered).
/// </summary>
public sealed class TenantContextTests
{
    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);

    private static Organization CreateOrganization(string slug = "northwind-labs")
        => Organization.Create(slug, slug, _createdAt);

    [Fact]
    public void Create_builds_a_context_from_a_matching_organization_subject_and_membership()
    {
        var organization = CreateOrganization();
        var userProfileId = Guid.CreateVersion7();
        var membership = OrganizationMember.Create(
            organization.Id,
            userProfileId,
            MembershipRole.CoHost,
            _createdAt);

        var context = TenantContext.Create(organization, userProfileId, membership);

        Assert.Equal(organization.Id, context.OrganizationId);
        Assert.Equal(organization.Slug, context.OrganizationSlug);
        Assert.Equal(userProfileId, context.UserProfileId);
        Assert.Equal(MembershipRole.CoHost, context.Role);
    }

    [Fact]
    public void Create_refuses_a_membership_belonging_to_a_foreign_organization()
    {
        // The membership belongs to a different organization than the one being
        // resolved: minting a context from it would carry a foreign tenant's
        // id. The guard fails closed (threat T5).
        var organization = CreateOrganization();
        var userProfileId = Guid.CreateVersion7();
        var foreignMembership = OrganizationMember.Create(
            Guid.CreateVersion7(),
            userProfileId,
            MembershipRole.Owner,
            _createdAt);

        Assert.Throws<ArgumentException>(
            () => TenantContext.Create(organization, userProfileId, foreignMembership));
    }

    [Fact]
    public void Create_refuses_a_membership_belonging_to_a_foreign_subject()
    {
        var organization = CreateOrganization();
        var membershipForAnotherSubject = OrganizationMember.Create(
            organization.Id,
            Guid.CreateVersion7(),
            MembershipRole.Owner,
            _createdAt);

        Assert.Throws<ArgumentException>(
            () => TenantContext.Create(organization, Guid.CreateVersion7(), membershipForAnotherSubject));
    }

    [Fact]
    public void HasRole_matches_only_the_exact_resolved_role()
    {
        var organization = CreateOrganization();
        var userProfileId = Guid.CreateVersion7();
        var membership = OrganizationMember.Create(
            organization.Id,
            userProfileId,
            MembershipRole.Auditor,
            _createdAt);
        var context = TenantContext.Create(organization, userProfileId, membership);

        Assert.True(context.HasRole(MembershipRole.Auditor));
        Assert.False(context.HasRole(MembershipRole.Owner));
        Assert.False(context.HasRole(MembershipRole.Participant));
    }

    [Fact]
    public void HasRole_rejects_an_undefined_role()
    {
        var organization = CreateOrganization();
        var userProfileId = Guid.CreateVersion7();
        var membership = OrganizationMember.Create(
            organization.Id,
            userProfileId,
            MembershipRole.Host,
            _createdAt);
        var context = TenantContext.Create(organization, userProfileId, membership);

        Assert.Throws<ArgumentOutOfRangeException>(() => context.HasRole((MembershipRole)999));
    }
}
