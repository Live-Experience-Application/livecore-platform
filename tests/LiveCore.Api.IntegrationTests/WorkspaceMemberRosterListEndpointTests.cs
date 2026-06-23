// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Workspaces;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the workspace member-roster read endpoint
/// (CORE-WSM-001, <c>GET /api/v1/workspaces/{workspaceId}/members</c>,
/// csv/api_routes.csv roles Owner,Admin). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/>, so the documented request flow (authentication -> tenant context
/// resolver -> endpoint -> inline authorization) is exercised end-to-end.
///
/// The story is the administration members screen under negative authorization, tenant isolation and
/// data-minimized PII discipline (threats T1/T5/T6/T7 in docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an Owner/Admin lists the workspace's members, each entry carrying the membership id, the generic
///   role and audience-safe display metadata, and a returned membership id drives <c>removeMember</c>;</item>
///   <item>the response NEVER contains an invited/login email, a token or any auth rationale (a payload-shape
///   assertion pins exactly the allow-listed fields);</item>
///   <item>the roster discloses the membership list, so a non-administration member, a non-member and a
///   cross-tenant/unknown workspace are all an indistinguishable hidden 404 (threats T1/T5);</item>
///   <item>the page clamps the limit to the platform maximum (CORE-DX-003).</item>
/// </list>
/// </summary>
public sealed class WorkspaceMemberRosterListEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static string ListRoute(Guid workspaceId, string organizationSlug)
        => $"/api/v1/workspaces/{workspaceId}/members?organizationSlug={organizationSlug}";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task List_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(ListRoute(Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: success for Owner/Admin, carries membership id + role + display name, drives removeMember ----

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task List_returns_the_roster_and_a_returned_id_drives_removeMember(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "admin-a";
        const string memberOneSubject = "member-one";
        const string memberTwoSubject = "member-two";
        const string memberOneDisplayName = "Ada Lovelace";
        const string memberOneEmail = "ada@example.test";
        Guid workspaceId = Guid.Empty;
        Guid memberOneUserId = Guid.Empty;
        Guid memberOneMembershipId = Guid.Empty;
        Guid memberTwoMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            // memberOne carries audience-safe display metadata AND an email, so the test can prove the email is
            // never projected while the display name is.
            var memberOne = await db.AddUserAsync(_issuer, memberOneSubject, memberOneDisplayName, memberOneEmail);
            memberOneUserId = memberOne.Id;
            var membershipOne = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, memberOne.Id, MembershipRole.Participant);
            memberOneMembershipId = membershipOne.Id;

            // memberTwo has NO display name, so the nullable display-metadata path is exercised too.
            var memberTwo = await db.AddUserAsync(_issuer, memberTwoSubject);
            var membershipTwo = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, memberTwo.Id, MembershipRole.Host);
            memberTwoMembershipId = membershipTwo.Id;
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>();
        Assert.NotNull(page);
        var members = page.Items;

        // Exactly the two seeded workspace members (the org-only caller is not a workspace member).
        Assert.Equal(
            new[] { memberOneMembershipId, memberTwoMembershipId }.OrderBy(id => id),
            members.Select(m => m.Id).OrderBy(id => id));

        // memberOne carries the membership id, the generic role, the userProfileId and the audience-safe
        // display name; the membership id is the value removeMember needs.
        var one = Assert.Single(members, m => m.Id == memberOneMembershipId);
        Assert.Equal(memberOneUserId, one.UserProfileId);
        Assert.Equal(nameof(MembershipRole.Participant), one.Role);
        Assert.Equal(memberOneDisplayName, one.DisplayName);
        Assert.Equal(workspaceId, one.WorkspaceId);

        // memberTwo has no display name, so its audience-safe display metadatum is null (never an error).
        var two = Assert.Single(members, m => m.Id == memberTwoMembershipId);
        Assert.Null(two.DisplayName);
        Assert.Equal(nameof(MembershipRole.Host), two.Role);

        // The roster gives the host the membership id removeMember requires: drive the removal with it.
        var removal = await client.DeleteAsync(
            $"/api/v1/workspaces/{workspaceId}/members/{memberOneMembershipId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);

        // The removed member is gone from a subsequent roster read; the other member remains.
        var after = await client.GetAsync(ListRoute(workspaceId, _orgA));
        var afterPage = await after.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>();
        Assert.NotNull(afterPage);
        Assert.Equal(new[] { memberTwoMembershipId }, afterPage.Items.Select(m => m.Id).ToArray());
    }

    // ---- payload shape: only the allow-listed fields; never email/token/auth-rationale (T6/T7) ----

    [Fact]
    public async Task List_payload_carries_only_allow_listed_fields_and_no_email_or_token()
    {
        await using var factory = new WorkspaceApiFactory();
        const string callerSubject = "owner-a";
        const string memberSubject = "member-a";
        const string memberDisplayName = "Grace Hopper";
        const string memberEmail = "grace@example.test";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            var member = await db.AddUserAsync(_issuer, memberSubject, memberDisplayName, memberEmail);
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, member.Id, MembershipRole.CoHost);
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // The display name (the allow-listed audience-safe metadatum) IS present...
        Assert.Contains(memberDisplayName, body);
        // ...but the subject's email and any token/auth-rationale are NEVER projected (T6/T7).
        Assert.DoesNotContain(memberEmail, body);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);

        // Pin the allow-listed property set: each roster entry carries EXACTLY these fields, so a future
        // leaking field (an email, a token, an auth rationale) fails this assertion.
        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        var keys = item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "createdAt",
                "displayName",
                "id",
                "organizationId",
                "role",
                "updatedAt",
                "userProfileId",
                "workspaceId",
            },
            keys);
    }

    // ---- 404 hidden: a non-administration tenant member (the roster hides the workspace) ----

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task List_is_404_for_a_non_administration_member(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var callerSubject = $"caller-{callerRole}";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, callerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            // The caller is even a workspace member, so this proves the read is hidden by ROLE, not by absence.
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, caller.Id, callerRole);
        });

        using var client = factory.CreateClientFor(callerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        // The roster discloses the membership list, so a non-administrator is hidden as 404, never 403.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 404 hidden: a caller who is not a member of the tenant at all ----

    [Fact]
    public async Task List_is_404_for_a_non_member_of_the_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string outsiderSubject = "outsider";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            // The outsider exists as a user but holds NO membership in org A.
            await db.AddUserAsync(_issuer, outsiderSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            var owner = await db.AddUserAsync(_issuer, "owner-a");
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(outsiderSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 404 hidden: cross-tenant / unknown (T1/T5) -------------------------

    [Fact]
    public async Task List_is_404_for_a_workspace_in_another_tenant()
    {
        // T5: the caller is an Owner of org A; the workspace exists in org B. Hidden as 404.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceInBId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, owner.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            workspaceInBId = workspaceInB.Id;
            var memberInB = await db.AddUserAsync(_issuer, "member-b");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, memberInB.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceInBId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_for_an_unknown_workspace_in_an_owned_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_is_404_when_the_token_org_claim_does_not_match_the_target_org()
    {
        // T5: the caller is an Owner of org A and names org A, but the token only asserts org B.
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgB);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- paging: the limit clamps to the platform maximum (CORE-DX-003) ------

    [Fact]
    public async Task List_clamps_the_limit_to_the_platform_maximum()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            var member = await db.AddUserAsync(_issuer, "member-a");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, member.Id, MembershipRole.Participant);
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA) + "&limit=100000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>();
        Assert.NotNull(page);
        // An oversized limit is clamped to the documented maximum (200), never rejected (CORE-DX-003).
        Assert.Equal(Page.MaxLimit, page.Limit);
    }

    [Fact]
    public async Task List_is_400_for_a_malformed_limit()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync(ListRoute(workspaceId, _orgA) + "&limit=not-a-number");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task List_is_400_when_the_organization_is_missing()
    {
        await using var factory = new WorkspaceApiFactory();
        const string ownerSubject = "owner-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var owner = await db.AddUserAsync(_issuer, ownerSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, owner.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(ownerSubject, _issuer, _orgA);
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/members");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
