using System.Net;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for organization member removal (CORE-LIFE-001,
/// <c>DELETE /api/v1/organizations/{organizationSlug}/members/{memberId}</c>,
/// csv/api_routes.csv roles Owner,Admin). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> audit) is exercised end-to-end.
///
/// The heart of the story is tenant access revocation under negative authorization and tenant isolation
/// (threats T1/T5/T6 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>a removed member loses tenant access on the NEXT request — the membership row is what the
///   <see cref="TenantContextResolver"/> requires, so removing it makes every tenant-scoped request fail
///   resolution (hidden 404);</item>
///   <item>the sole organization Owner cannot be removed (the last-Owner invariant, 409 — an ownerless
///   tenant would be permanently unreachable);</item>
///   <item>401 for missing auth; 403 for an org member that is not Owner/Admin; 404 (hidden) for a
///   cross-tenant/unknown organization or member;</item>
///   <item>every successful removal appends exactly one append-only <c>MemberRemoved</c> audit record.</item>
/// </list>
/// </summary>
public sealed class OrganizationMemberRemovalEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Remove_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success for Owner/Admin, revokes tenant access, audits --------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Remove_succeeds_for_owner_or_admin_revokes_access_and_audits(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        const string targetSubject = "target-a";
        Guid adminProfileId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);

            var target = await db.AddUserAsync(_issuer, targetSubject);
            var membership = await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        // Before: the target can resolve the tenant (it is a member), so a tenant-scoped read succeeds.
        using (var targetBefore = factory.CreateClientFor(targetSubject, _issuer, _orgA))
        {
            var before = await targetBefore.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The membership row is gone.
        Assert.False(await MembershipExistsAsync(factory, targetMembershipId));

        // After: the removed member loses tenant access on the next request (resolution now denies -> 404).
        using (var targetAfter = factory.CreateClientFor(targetSubject, _issuer, _orgA))
        {
            var after = await targetAfter.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }

        // The removal is audited exactly once: organization-level (no workspace), capturing the actor, the
        // removed membership and the revoked role.
        var audit = Assert.Single(await ListMemberRemovedAsync(factory));
        Assert.Equal(AuditAction.MemberRemoved, audit.Action);
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Null(audit.WorkspaceId);
        Assert.Equal(nameof(OrganizationMember), audit.ResourceType);
        Assert.Equal(targetMembershipId, audit.ResourceId);
        Assert.Equal(nameof(MembershipRole.Participant), audit.PreviousState);
        Assert.Null(audit.NewState);
        Assert.Null(audit.TargetParticipantId);
    }

    // ---- 403: authenticated org member without Owner/Admin -------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Remove_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    // ---- 404 hidden: cross-tenant / unknown (T1/T5) -------------------------

    [Fact]
    public async Task Remove_is_404_for_a_member_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the member exists in org B. Addressing it through org A is
        // hidden as 404 (the member id belongs to a tenant the caller cannot see).
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid membershipInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);
            var target = await db.AddUserAsync(_issuer, "target-b");
            var membership = await db.AddOrganizationMemberAsync(orgB.Id, target.Id, MembershipRole.Participant);
            membershipInBId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{membershipInBId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, membershipInBId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_is_404_for_an_unknown_member_in_the_callers_org()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Remove_is_404_when_the_caller_is_not_a_member_of_the_target_org()
    {
        // The token claims org A, but the caller has NO membership in org A. Resolution denies (no
        // membership), hidden as 404 — the caller cannot even see the tenant, let alone manage it.
        await using var factory = new WorkspaceApiFactory();
        const string strangerSubject = "stranger";
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            await db.AddUserAsync(_issuer, strangerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(strangerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_is_404_when_the_token_org_claim_does_not_match_the_path_org()
    {
        // T5: the caller is an Owner of org A and names org A in the path, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
    }

    // ---- 409: last-Owner invariant ------------------------------------------

    [Fact]
    public async Task Remove_is_409_for_the_sole_organization_owner_and_changes_nothing()
    {
        // The only Owner of the tenant cannot be removed: an ownerless organization would be permanently
        // unreachable. The caller is that sole Owner and tries to remove their own membership.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid ownerMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var membership = await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            ownerMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{ownerMembershipId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, ownerMembershipId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_succeeds_for_an_owner_when_another_organization_owner_remains()
    {
        // Two Owners: removing one leaves an Owner, so the last-Owner invariant does not block it.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "owner-1";
        Guid ownerTwoMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var ownerOne = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, ownerOne.Id, MembershipRole.Owner);
            var ownerTwo = await db.AddUserAsync(_issuer, "owner-2");
            var membershipTwo = await db.AddOrganizationMemberAsync(org.Id, ownerTwo.Id, MembershipRole.Owner);
            ownerTwoMembershipId = membershipTwo.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/organizations/{_orgA}/members/{ownerTwoMembershipId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await MembershipExistsAsync(factory, ownerTwoMembershipId));
        var audit = Assert.Single(await ListMemberRemovedAsync(factory));
        Assert.Equal(nameof(MembershipRole.Owner), audit.PreviousState);
    }

    private static async Task<bool> MembershipExistsAsync(WorkspaceApiFactory factory, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.OrganizationMembers.AsNoTracking().AnyAsync(member => member.Id == membershipId);
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListMemberRemovedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditAction.MemberRemoved)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }
}
