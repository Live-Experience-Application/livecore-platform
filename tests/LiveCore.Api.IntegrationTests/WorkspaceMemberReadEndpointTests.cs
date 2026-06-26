// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the single-member read-with-ETag command (CORE-WSM-003,
/// <c>GET /api/v1/workspaces/{workspaceId}/members/{memberId}</c>, csv/api_routes.csv roles Owner,Admin). They
/// drive the real application over real HTTP through <see cref="WorkspaceApiFactory"/>, so the documented request
/// flow (authentication -> tenant context resolver -> endpoint -> inline authorization) is exercised end-to-end.
///
/// The route exposes a member's per-member optimistic-concurrency token so a vertical can make the role-change
/// PATCH a true before-the-write conditional write (ARC-GAP-110): it could previously catch a raced 409 but not a
/// stale-read 412, because no per-member token was obtainable through the SDK (the roster keeps its
/// no-per-item-ETag collection contract, CORE-DX-002/003). The tests cover the read, the read-then-conditional
/// PATCH flow, and negative authorization / tenant isolation (threats T1/T5/T6/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>an Owner/Admin reads a member and the response carries the data-minimized membership projection (no
///   email, no token) plus — on PostgreSQL — the per-member weak <c>ETag</c>;</item>
///   <item>the token read from the GET makes a conditional PATCH succeed, while a stale <c>If-Match</c> is
///   refused with 412 and changes nothing (CORE-DX-002);</item>
///   <item>401 for missing auth; 404 (hidden) for a non-administration tenant member, a cross-tenant/unknown
///   workspace, an unknown member, or a member in another workspace — the read discloses membership, so it
///   mirrors the roster's hidden-404 posture rather than the PATCH's 403; 400 for a missing organization.</item>
/// </list>
/// </summary>
public sealed class WorkspaceMemberReadEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static string ReadRoute(Guid workspaceId, Guid memberId, string organizationSlug)
        => $"/api/v1/workspaces/{workspaceId}/members/{memberId}?organizationSlug={organizationSlug}";

    // ---- 401: missing / invalid auth ----------------------------------------

    [Fact]
    public async Task Read_without_a_token_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await client.GetAsync(ReadRoute(Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- 200: success for Owner/Admin; data-minimized body + per-member ETag --

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Read_succeeds_for_org_owner_or_admin_and_carries_the_member_and_its_etag(MembershipRole callerRole)
    {
        await using var factory = new WorkspaceApiFactory();
        const string adminSubject = "admin-a";
        Guid targetProfileId = Guid.Empty;
        Guid workspaceId = Guid.Empty;
        Guid targetMembershipId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var admin = await db.AddUserAsync(_issuer, adminSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, admin.Id, callerRole);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;

            var target = await db.AddUserAsync(_issuer, "target-a");
            targetProfileId = target.Id;
            var membership = await db.AddWorkspaceMemberAsync(
                org.Id, workspace.Id, target.Id, MembershipRole.Participant);
            targetMembershipId = membership.Id;
        });

        using var admin = factory.CreateClientFor(adminSubject, _issuer, _orgA);
        var response = await admin.GetAsync(ReadRoute(workspaceId, targetMembershipId, _orgA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The body is the generic membership projection: the membership id, the subject and the generic role.
        var body = await response.Content.ReadFromJsonAsync<WorkspaceMemberResponse>(_json);
        Assert.NotNull(body);
        Assert.Equal(targetMembershipId, body!.Id);
        Assert.Equal(targetProfileId, body.UserProfileId);
        Assert.Equal(workspaceId, body.WorkspaceId);
        Assert.Equal(nameof(MembershipRole.Participant), body.Role);

        // Data minimization (threats T6/T7): the projection carries no email and no token — there is no field
        // for either, so neither can appear in the wire body.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("@", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);

        if (PostgresTestDatabase.IsConfigured)
        {
            // The single-resource read carries the per-member weak ETag (the xmin token), so a consumer can echo
            // it back as If-Match on the role-change PATCH (CORE-DX-002).
            var etag = response.Headers.ETag;
            Assert.NotNull(etag);
            Assert.True(etag!.IsWeak);
        }
        else
        {
            // SQLite maps no row-version token, so there is none to surface.
            Assert.Null(response.Headers.ETag);
        }
    }

    // ---- The acceptance flow: read the member's token, then conditionally PATCH

    [Fact]
    public async Task Read_member_then_conditional_role_change_is_412_when_stale_and_succeeds_when_fresh()
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

        // Read the member with its ETag — the per-member token CORE-WSM-003 newly makes obtainable.
        var read = await client.GetAsync(ReadRoute(workspaceId, targetMembershipId, _orgA));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var freshEtag = ETagOf(read);

        // A clearly-stale If-Match is refused with 412 BEFORE the write, on both providers (W/"0" is never the
        // current token: xmin is never 0 on PostgreSQL, and on SQLite an If-Match cannot be confirmed).
        var stale = await PatchRoleAsync(
            client, workspaceId, targetMembershipId, MembershipRole.Admin, ifMatch: "W/\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("precondition_failed", await ReadCodeAsync(stale));
        Assert.Equal(MembershipRole.Participant, await RoleInDbAsync(factory, targetMembershipId));

        if (PostgresTestDatabase.IsConfigured)
        {
            // The fresh token just read makes the same conditional role change succeed — the before-the-write
            // conditional path the per-member token unlocks.
            Assert.False(string.IsNullOrEmpty(freshEtag));
            var fresh = await PatchRoleAsync(
                client, workspaceId, targetMembershipId, MembershipRole.Admin, ifMatch: freshEtag);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            Assert.Equal(MembershipRole.Admin, await RoleInDbAsync(factory, targetMembershipId));
        }
        else
        {
            // SQLite maps no row-version token, so the read emits no ETag to echo back.
            Assert.Null(freshEtag);
        }
    }

    // ---- 404 hidden: a non-administration tenant member ---------------------

    [Theory]
    [InlineData(MembershipRole.Host)]
    [InlineData(MembershipRole.CoHost)]
    [InlineData(MembershipRole.Participant)]
    [InlineData(MembershipRole.Observer)]
    [InlineData(MembershipRole.Auditor)]
    public async Task Read_is_hidden_404_for_an_org_member_that_is_not_owner_or_admin(MembershipRole callerRole)
    {
        // The read discloses a member's standing, so — like the roster (CORE-WSM-001), and UNLIKE the PATCH which
        // reveals existence with a 403 — a non-administration caller is hidden as 404: the workspace's existence is
        // never revealed to a non-administrator (threats T1/T5).
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
        var response = await client.GetAsync(ReadRoute(workspaceId, targetMembershipId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 404 hidden: cross-tenant / unknown / foreign-workspace (T1/T5) -----

    [Fact]
    public async Task Read_is_404_for_a_workspace_in_another_tenant()
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
        var response = await client.GetAsync(ReadRoute(workspaceInBId, membershipInBId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_workspace()
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
        var response = await client.GetAsync(ReadRoute(Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_an_unknown_member_in_an_owned_workspace()
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
        var response = await client.GetAsync(ReadRoute(workspaceId, Guid.CreateVersion7(), _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_for_a_member_in_another_workspace_of_the_same_tenant()
    {
        // T1/T5: the member id exists, but in a DIFFERENT workspace of the same tenant. Addressing it through the
        // wrong workspace must never resolve it.
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
        var response = await client.GetAsync(ReadRoute(otherWorkspaceId, membershipInWorkspaceOneId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_is_404_when_the_token_org_claim_does_not_match_the_target_org()
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
        var response = await client.GetAsync(ReadRoute(workspaceId, targetMembershipId, _orgA));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- 400: missing organization ------------------------------------------

    [Fact]
    public async Task Read_is_400_when_the_organization_is_missing()
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
        // No organizationSlug query parameter.
        var response = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/members/{targetMembershipId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------

    private static Task<HttpResponseMessage> PatchRoleAsync(
        HttpClient client,
        Guid workspaceId,
        Guid memberId,
        MembershipRole role,
        string? ifMatch)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/workspaces/{workspaceId}/members/{memberId}")
        {
            Content = JsonContent.Create(new UpdateWorkspaceMemberRoleRequest(_orgA, role.ToString())),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return client.SendAsync(request);
    }

    private static string? ETagOf(HttpResponseMessage response)
        => response.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault() : null;

    private static async Task<MembershipRole?> RoleInDbAsync(WorkspaceApiFactory factory, Guid membershipId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var member = await context.WorkspaceMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId);
        return member?.Role;
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }
}
