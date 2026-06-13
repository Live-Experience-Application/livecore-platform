using System.Net;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for workspace member removal (CORE-LIFE-001,
/// <c>DELETE /api/v1/workspaces/{workspaceId}/members/{memberId}</c>,
/// csv/api_routes.csv roles Owner,Admin). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization -> audit) is exercised end-to-end.
///
/// The heart of the story is access revocation under negative authorization and tenant isolation
/// (threats T1/T5/T6 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>a removed member loses access on the NEXT request (the membership row IS the grant);</item>
///   <item>the sole workspace Owner cannot be removed (the last-Owner invariant, 409);</item>
///   <item>401 for missing auth; 403 for an org member that is not Owner/Admin; 404 (hidden) for a
///   cross-tenant/unknown workspace, an unknown member, or a member in another workspace; 400 for a
///   missing organization;</item>
///   <item>every successful removal appends exactly one append-only <c>MemberRemoved</c> audit record.</item>
/// </list>
/// </summary>
public sealed class WorkspaceMemberRemovalEndpointTests
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
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 204: success for Owner/Admin, revokes access, audits ---------------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Remove_succeeds_for_org_owner_or_admin_revokes_access_and_audits(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        const string targetSubject = "target-a";
        Guid adminProfileId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            adminProfileId = admin.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            // The target is a tenant member (so it can resolve the tenant) and a workspace member (so it
            // currently has workspace access to lose).
            var target = await db.AddUserAsync(_issuer, targetSubject);
            await db.AddOrganizationMemberAsync(org.Id, target.Id, MembershipRole.Participant);
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        // Before: the target can read the workspace it belongs to.
        using (var targetBefore = factory.CreateClientFor(targetSubject, _issuer, _orgA))
        {
            var before = await targetBefore.GetAsync(
                $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The membership row is gone.
        Assert.False(await MembershipExistsAsync(factory, targetMembershipId));

        // After: the removed member loses workspace access on the next request (hidden 404).
        using (var targetAfter = factory.CreateClientFor(targetSubject, _issuer, _orgA))
        {
            var after = await targetAfter.GetAsync(
                $"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
            Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        }

        // The removal is audited exactly once, capturing the actor, the removed membership and the role.
        var audit = Assert.Single(await ListMemberRemovedAsync(factory));
        Assert.Equal(AuditAction.MemberRemoved, audit.Action);
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(nameof(WorkspaceMember), audit.ResourceType);
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
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The membership is untouched and nothing is audited.
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    // ---- 404 hidden: cross-tenant / unknown / foreign-workspace (T1/T5) -----

    [Fact]
    public async Task Remove_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace and member exist in org B. Hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceInBId = Guid.Empty;
        Guid membershipInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, admin.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            var target = await db.AddUserAsync(_issuer, "target-b");
            var membership = await db.AddWorkspaceMemberAsync(
                orgB.Id, workspaceInB.Id, target.Id, MembershipRole.Participant);
            membershipInBId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceInBId}/members/{membershipInBId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, membershipInBId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_is_404_for_an_unknown_workspace()
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
            $"/api/v1/workspaces/{Guid.CreateVersion7()}/members/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Remove_is_404_for_an_unknown_member_in_an_owned_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{Guid.CreateVersion7()}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Remove_is_404_for_a_member_in_another_workspace_of_the_same_tenant()
    {
        // T1/T5: the member id exists, but in a DIFFERENT workspace of the same tenant. Addressing it
        // through the wrong workspace must never resolve it.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid otherWorkspaceId = Guid.Empty;
        Guid membershipInWorkspaceOneId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspaceOne = await db.AddWorkspaceAsync(org.Id, "show-one", "Show One");
            var workspaceTwo = await db.AddWorkspaceAsync(org.Id, "show-two", "Show Two");
            otherWorkspaceId = workspaceTwo.Id;
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspaceOne.Id, target.Id, MembershipRole.Participant);
            membershipInWorkspaceOneId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        // Address workspace one's membership through workspace two.
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{otherWorkspaceId}/members/{membershipInWorkspaceOneId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, membershipInWorkspaceOneId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is an Owner of org A and names org A, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        // Token asserts only org B.
        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgB);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task Remove_is_400_when_the_organization_is_missing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, targetMembershipId));
    }

    // ---- 409: last-Owner invariant ------------------------------------------

    [Fact]
    public async Task Remove_is_409_for_the_sole_workspace_owner_and_changes_nothing()
    {
        // The caller is an org Owner (so the removal is authorized), and the target is the workspace's ONLY
        // Owner. Removing it would leave the workspace ownerless, so it is refused with 409.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceId = Guid.Empty;
        Guid soleOwnerMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var owner = await db.AddUserAsync(_issuer, "ws-owner");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
            soleOwnerMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{soleOwnerMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await MembershipExistsAsync(factory, soleOwnerMembershipId));
        Assert.Empty(await ListMemberRemovedAsync(factory));
    }

    [Fact]
    public async Task Remove_succeeds_for_an_owner_when_another_workspace_owner_remains()
    {
        // Two workspace Owners: removing one leaves an Owner, so the last-Owner invariant does not block it.
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid workspaceId = Guid.Empty;
        Guid ownerOneMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var ownerOne = await db.AddUserAsync(_issuer, "ws-owner-1");
            var ownerTwo = await db.AddUserAsync(_issuer, "ws-owner-2");
            var membershipOne = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, ownerOne.Id, MembershipRole.Owner);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, ownerTwo.Id, MembershipRole.Owner);
            ownerOneMembershipId = membershipOne.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{ownerOneMembershipId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await MembershipExistsAsync(factory, ownerOneMembershipId));
        var audit = Assert.Single(await ListMemberRemovedAsync(factory));
        Assert.Equal(nameof(MembershipRole.Owner), audit.PreviousState);
    }

    private static async Task<bool> MembershipExistsAsync(WorkspaceApiFactory factory, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.WorkspaceMembers.AsNoTracking().AnyAsync(member => member.Id == membershipId);
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
