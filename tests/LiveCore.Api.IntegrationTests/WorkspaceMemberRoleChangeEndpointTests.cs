// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using LiveCore.Api.Audit;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace member role-change command (CORE-WSM-002,
/// <c>PATCH /api/v1/workspaces/{workspaceId}/members/{memberId}</c>, csv/api_routes.csv roles Owner,Admin).
/// They drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the documented
/// request flow (authentication -> tenant context resolver -> endpoint -> inline authorization -> audit) is
/// exercised end-to-end.
///
/// The story is role administration under negative authorization, tenant isolation, the last-Owner invariant
/// and optimistic concurrency (threats T1/T5/T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an Owner/Admin changes another member's role and the roster reflects it, emitting one
///   <c>MemberRoleChanged</c> audit fact with the before/after role;</item>
///   <item>demoting the last remaining workspace Owner is refused (409), mirroring the
///   last-Owner-cannot-be-removed rule;</item>
///   <item>a stale <c>If-Match</c> is rejected with 412 and changes nothing (CORE-DX-002);</item>
///   <item>401 for missing auth; 403 for an org member that is not Owner/Admin; 404 (hidden) for a
///   cross-tenant/unknown workspace, an unknown member, or a member in another workspace; 400 for a missing
///   organization or an invalid role;</item>
///   <item>a same-role change is an idempotent no-op (200, no audit).</item>
/// </list>
/// </summary>
public sealed class WorkspaceMemberRoleChangeEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static string Route(Guid workspaceId, Guid memberId)
        => $"/api/v1/workspaces/{workspaceId}/members/{memberId}";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Change_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PatchRoleAsync(
            client, Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: success for Owner/Admin, roster reflects it, audits -----------

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Change_succeeds_for_org_owner_or_admin_roster_reflects_it_and_audits(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
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

            var target = await db.AddUserAsync(_issuer, "target-a");
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await PatchRoleAsync(admin, workspaceId, targetMembershipId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The returned membership carries the new role.
        var body = await response.Content.ReadFromJsonAsync<WorkspaceMemberResponse>();
        Assert.NotNull(body);
        Assert.Equal(nameof(MembershipRole.Admin), body!.Role);
        Assert.Equal(targetMembershipId, body.Id);

        // The persisted membership has the new role.
        Assert.Equal(MembershipRole.Admin, await RoleInDbAsync(factory, targetMembershipId));

        // The administration roster reflects the change.
        Assert.Equal(nameof(MembershipRole.Admin), await RosterRoleAsync(admin, workspaceId, targetMembershipId));

        // The change is audited exactly once, capturing the actor, the membership and the before/after role.
        var audit = Assert.Single(await ListRoleChangedAsync(factory));
        Assert.Equal(AuditAction.MemberRoleChanged, audit.Action);
        Assert.Equal(adminProfileId, audit.ActorUserProfileId);
        Assert.Equal(workspaceId, audit.WorkspaceId);
        Assert.Equal(nameof(WorkspaceMember), audit.ResourceType);
        Assert.Equal(targetMembershipId, audit.ResourceId);
        Assert.Equal(nameof(MembershipRole.Participant), audit.PreviousState);
        Assert.Equal(nameof(MembershipRole.Admin), audit.NewState);
        Assert.Null(audit.TargetParticipantId);
    }

    // ---- 200: same-role change is an idempotent no-op, no audit -------------

    [Fact]
    public async Task Change_to_the_same_role_is_a_no_op_200_and_writes_no_audit()
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
                org.Id, workspace.Id, target.Id, MembershipRole.Admin);
            targetMembershipId = membership.Id;
        });

        using var client = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await PatchRoleAsync(client, workspaceId, targetMembershipId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MembershipRole.Admin, await RoleInDbAsync(factory, targetMembershipId));
        // A no-op records no transition.
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    // ---- 403: authenticated org member without Owner/Admin -------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Change_is_403_for_an_org_member_that_is_not_owner_or_admin(MembershipRole callerRole)
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
        var response = await PatchRoleAsync(client, workspaceId, targetMembershipId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The membership role is untouched and nothing is audited.
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    // ---- 404 hidden: cross-tenant / unknown / foreign-workspace (T1/T5) -----

    [Fact]
    public async Task Change_is_404_for_a_workspace_in_another_tenant()
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
        var response = await PatchRoleAsync(client, workspaceInBId, membershipInBId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, membershipInBId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    [Fact]
    public async Task Change_is_404_for_an_unknown_workspace()
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
        var response = await PatchRoleAsync(
            client, Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Change_is_404_for_an_unknown_member_in_an_owned_workspace()
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
        var response = await PatchRoleAsync(client, workspaceId, Guid.CreateVersion7(), _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Change_is_404_for_a_member_in_another_workspace_of_the_same_tenant()
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
        var response = await PatchRoleAsync(
            client, otherWorkspaceId, membershipInWorkspaceOneId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, membershipInWorkspaceOneId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    [Fact]
    public async Task Change_is_404_when_the_token_org_claim_does_not_match_the_target_org()
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
        var response = await PatchRoleAsync(client, workspaceId, targetMembershipId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));
    }

    // ---- 400: missing organization / invalid role ---------------------------

    [Fact]
    public async Task Change_is_400_when_the_organization_is_missing()
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
        var response = await PatchAsync(
            client, workspaceId, targetMembershipId,
            new UpdateWorkspaceMemberRoleRequest(OrganizationSlug: null, Role: nameof(MembershipRole.Admin)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));
    }

    [Fact]
    public async Task Change_is_400_for_an_invalid_role()
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
        // A non-defined role name (never a MembershipRole) is rejected, so an undefined role can never be
        // smuggled in (threat T6 role limitation).
        var response = await PatchAsync(
            client, workspaceId, targetMembershipId,
            new UpdateWorkspaceMemberRoleRequest(_orgA, "Superuser"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    // ---- 412: a stale If-Match is refused, fail-closed ----------------------

    [Fact]
    public async Task Change_with_a_stale_if_match_is_412_and_changes_nothing()
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
        // W/"0" can never be the current token: xmin is never 0 on PostgreSQL, and on SQLite (no token) an
        // If-Match cannot be confirmed — both fail closed to 412.
        var response = await PatchRoleAsync(
            client, workspaceId, targetMembershipId, _orgA, MembershipRole.Admin, ifMatch: "W/\"0\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        // The rejected write changed nothing and audited nothing.
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    // ---- 409: last-Owner invariant on demotion ------------------------------

    [Fact]
    public async Task Change_is_409_when_demoting_the_sole_workspace_owner_and_changes_nothing()
    {
        // The caller is an org Owner (so the change is authorized), and the target is the workspace's ONLY
        // Owner. Demoting it would leave the workspace ownerless, so it is refused with 409.
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
        var response = await PatchRoleAsync(
            client, workspaceId, soleOwnerMembershipId, _orgA, MembershipRole.Participant);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(MembershipRole.Owner, await RoleInDbAsync(factory, soleOwnerMembershipId));
        Assert.Empty(await ListRoleChangedAsync(factory));
    }

    [Fact]
    public async Task Change_demotes_an_owner_when_another_workspace_owner_remains()
    {
        // Two workspace Owners: demoting one leaves an Owner, so the last-Owner invariant does not block it.
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
        var response = await PatchRoleAsync(client, workspaceId, ownerOneMembershipId, _orgA, MembershipRole.Admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MembershipRole.Admin, await RoleInDbAsync(factory, ownerOneMembershipId));
        var audit = Assert.Single(await ListRoleChangedAsync(factory));
        Assert.Equal(nameof(MembershipRole.Owner), audit.PreviousState);
        Assert.Equal(nameof(MembershipRole.Admin), audit.NewState);
    }

    private static Task<HttpResponseMessage> PatchRoleAsync(
        HttpClient client,
        Guid workspaceId,
        Guid memberId,
        string organizationSlug,
        MembershipRole role,
        string? ifMatch = null)
        => PatchAsync(
            client,
            workspaceId,
            memberId,
            new UpdateWorkspaceMemberRoleRequest(organizationSlug, role.ToString()),
            ifMatch);

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        Guid workspaceId,
        Guid memberId,
        UpdateWorkspaceMemberRoleRequest body,
        string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, Route(workspaceId, memberId))
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static async Task<MembershipRole?> RoleInDbAsync(WorkspaceApiFactory factory, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var member = await context.WorkspaceMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId);
        return member?.Role;
    }

    private static async Task<string?> RosterRoleAsync(HttpClient client, Guid workspaceId, Guid membershipId)
    {
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/members?organizationSlug={_orgA}");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>();
        return page!.Items.Single(entry => entry.Id == membershipId).Role;
    }

    private static async Task<IReadOnlyList<AuditLogEntry>> ListRoleChangedAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AuditLogs.AsNoTracking()
            .Where(entry => entry.Action == AuditAction.MemberRoleChanged)
            .OrderBy(entry => entry.Id)
            .ToListAsync();
    }
}
