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
/// HTTP integration tests for the workspace creation founding-membership behavior (CORE-WS-009): a
/// <c>POST /api/v1/workspaces</c> enrolls the creating principal as the new workspace's founding
/// <see cref="MembershipRole.Owner"/> member, atomically in the same unit of work as the workspace insert
/// (mirroring how org create makes the caller the tenant's founding Owner). They drive the real application over
/// real HTTP through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign
/// keys ON), so the documented request flow is exercised end-to-end.
///
/// The story's required tests:
/// <list type="bullet">
///   <item>POSITIVE — the creator immediately sees the workspace they just made: it is present in the
///   membership-scoped list (<c>GET /api/v1/workspaces</c>), readable by id (<c>GET
///   /api/v1/workspaces/{id}</c> is 200) and the creator is the lone <c>Owner</c> on the member roster
///   (<c>GET /api/v1/workspaces/{id}/members</c>) — where before this story both the list and the by-id read
///   were empty/404 because no membership row was written.</item>
///   <item>NEGATIVE — authorization is NOT loosened: a different tenant principal who did not create the
///   workspace (even an organization Admin, who could create their own) still gets an empty list and a hidden
///   404 by id until they are invited (threats T1/T5).</item>
///   <item>IDEMPOTENT — a retried create under the same <c>Idempotency-Key</c> replays the original workspace
///   and writes exactly ONE founding member (the unique <c>(workspace_id, user_id)</c> index backstop).</item>
/// </list>
/// All fixtures are generic Core vocabulary (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class WorkspaceCreationFoundingMembershipEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _header = "Idempotency-Key";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- POSITIVE: the creator is the founding Owner and immediately sees the workspace ----

    [Theory]
    [InlineData(MembershipRole.Owner)]
    [InlineData(MembershipRole.Admin)]
    public async Task Create_enrolls_the_creator_as_the_founding_owner_and_they_immediately_see_the_workspace(
        MembershipRole orgRole)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"creator-{orgRole}";
        Guid creatorUserId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            creatorUserId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, orgRole);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);

        // Create the workspace: 201.
        var create = await client.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workspaceId = (await ReadWorkspaceAsync(create)).Id;
        Assert.NotEqual(Guid.Empty, workspaceId);

        // The membership-scoped list now contains the new workspace (before CORE-WS-009 it was empty).
        var list = await client.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
        var listed = Assert.Single(page.Items);
        Assert.Equal(workspaceId, listed.Id);
        Assert.Equal("summer-show", listed.Slug);

        // The by-id read is 200 for the creator (before CORE-WS-009 it was a hidden 404).
        var byId = await client.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal(workspaceId, (await ReadWorkspaceAsync(byId)).Id);

        // The roster shows exactly the creator, as the Owner member.
        var members = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/members?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        var roster = await members.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>(_json);
        Assert.NotNull(roster);
        var founder = Assert.Single(roster.Items);
        Assert.Equal(creatorUserId, founder.UserProfileId);
        Assert.Equal(nameof(MembershipRole.Owner), founder.Role);
        Assert.Equal(workspaceId, founder.WorkspaceId);

        // Exactly one membership row was written for the new workspace.
        Assert.Equal(1, await CountWorkspaceMembersAsync(factory, workspaceId));
    }

    // ---- NEGATIVE: a different tenant principal still sees nothing until invited (T1/T5) ----

    [Fact]
    public async Task A_non_creating_tenant_principal_sees_an_empty_list_and_a_404_until_invited()
    {
        // The "other" principal is an organization ADMIN of the same tenant — privileged enough to create their
        // OWN workspaces — yet they are NOT auto-enrolled in a workspace someone else created. Authorization is
        // not loosened: they see nothing for that workspace until invited.
        await using var factory = new WorkspaceApiFactory();
        const string creatorSubject = "creator-owner";
        const string otherSubject = "other-admin";
        await factory.SeedAsync(async db =>
        {
            var creator = await db.AddUserAsync(_issuer, creatorSubject);
            var other = await db.AddUserAsync(_issuer, otherSubject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, creator.Id, MembershipRole.Owner);
            await db.AddOrganizationMemberAsync(org.Id, other.Id, MembershipRole.Admin);
        });

        using var creatorClient = factory.CreateClientFor(creatorSubject, _issuer, _orgA);
        var create = await creatorClient.PostAsJsonAsync(
            "/api/v1/workspaces",
            new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workspaceId = (await ReadWorkspaceAsync(create)).Id;

        using var otherClient = factory.CreateClientFor(otherSubject, _issuer, _orgA);

        // The other principal is a member of the tenant, so the list resolves (200) but is membership-scoped:
        // it never lists a workspace they are not a member of.
        var list = await otherClient.GetAsync($"/api/v1/workspaces?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<PageDto<WorkspaceDto>>(_json);
        Assert.NotNull(page);
        Assert.Empty(page.Items);

        // The by-id read is a hidden 404 (object-level authorization; not 403, so existence is not leaked).
        var byId = await otherClient.GetAsync($"/api/v1/workspaces/{workspaceId}?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
    }

    // ---- IDEMPOTENT: a retried create under the same key writes exactly one founding member ----

    [Fact]
    public async Task A_retried_create_under_the_same_idempotency_key_writes_exactly_one_founding_member()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "creator-owner";
        Guid creatorUserId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            creatorUserId = user.Id;
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Owner);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var body = new CreateWorkspaceRequest(_orgA, "summer-show", "Summer Show");

        var first = await PostWithKeyAsync(client, "/api/v1/workspaces", body, "ws-key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var workspaceId = (await ReadWorkspaceAsync(first)).Id;

        // The retry under the SAME key replays the original workspace (200, same id), not a second create.
        var replay = await PostWithKeyAsync(client, "/api/v1/workspaces", body, "ws-key-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(workspaceId, (await ReadWorkspaceAsync(replay)).Id);

        // Exactly ONE founding member exists for the workspace (no duplicate on the unique index).
        Assert.Equal(1, await CountWorkspaceMembersAsync(factory, workspaceId));

        var members = await client.GetAsync($"/api/v1/workspaces/{workspaceId}/members?organizationSlug={_orgA}");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        var roster = await members.Content.ReadFromJsonAsync<PageDto<WorkspaceMemberRosterEntryResponse>>(_json);
        Assert.NotNull(roster);
        var founder = Assert.Single(roster.Items);
        Assert.Equal(creatorUserId, founder.UserProfileId);
        Assert.Equal(nameof(MembershipRole.Owner), founder.Role);
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client,
        string url,
        object body,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: _json),
        };
        request.Headers.Add(_header, idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<int> CountWorkspaceMembersAsync(WorkspaceApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.WorkspaceMembers.AsNoTracking().CountAsync(m => m.WorkspaceId == workspaceId);
    }

    private static async Task<WorkspaceDto> ReadWorkspaceAsync(HttpResponseMessage response)
    {
        var dto = await response.Content.ReadFromJsonAsync<WorkspaceDto>(_json);
        Assert.NotNull(dto);
        return dto;
    }

    private sealed record WorkspaceDto(Guid Id, Guid OrganizationId, string Slug, string Name);
}
