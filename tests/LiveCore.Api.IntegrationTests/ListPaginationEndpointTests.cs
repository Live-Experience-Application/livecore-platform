using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Organizations;
using LiveCore.Api.Sessions;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the bounded-list pagination contract (CORE-DX-003, the "API Contract and Consumer
/// Ergonomics" epic). Every list endpoint accepts optional <c>limit</c>/<c>offset</c> clamped to a documented
/// maximum and returns the stable <c>items + hasMore</c> page envelope, so no list returns an unbounded array
/// (consumability + DoS, threat T9). These drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON).
///
/// Coverage (the story's required tests), exercised against a representative cross-section of the bounded list
/// endpoints (the session list, the entity list and the scene list — covering the non-projected and the
/// role-projected shapes):
/// <list type="bullet">
///   <item>CLAMP: a request for a page larger than the server maximum is clamped to it (the response's effective
///   <c>limit</c> is the maximum, never the oversized requested value).</item>
///   <item>PAGING: <c>hasMore</c> and the page contents are correct across pages (first/middle/last), and the
///   pages stitch back to the full, deterministically-ordered set with no gaps or overlaps.</item>
///   <item>OVER-LARGE WORKSPACE: a workspace with more resources than the default page size does NOT return an
///   unbounded payload — the default page is capped at the default size with <c>hasMore</c> true.</item>
///   <item>MALFORMED: a present-but-malformed <c>limit</c>/<c>offset</c> is a 400 (only after authorization).</item>
/// </list>
/// </summary>
public sealed class ListPaginationEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---- CLAMP: an oversized limit is reduced to the server maximum ----------

    [Fact]
    public async Task Session_list_clamps_an_oversized_limit_to_the_maximum()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            await db.AddSessionAsync(org.Id, workspace.Id, "S", SessionStatus.Prepared);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}&limit={Page.MaxLimit + 5000}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<SessionItem>>(_json);
        Assert.NotNull(page);
        // The effective limit is the server maximum, never the requested oversized value.
        Assert.Equal(Page.MaxLimit, page.Limit);
        Assert.Single(page.Items);
        Assert.False(page.HasMore);
    }

    // ---- PAGING: hasMore + contents correct across pages --------------------

    [Fact]
    public async Task Session_list_pages_through_the_workspace_with_limit_and_offset()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        var expected = new List<Guid>();
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            for (var i = 0; i < 5; i++)
            {
                expected.Add((await db.AddSessionAsync(org.Id, workspace.Id, $"S{i}", SessionStatus.Prepared)).Id);
            }
        });

        // The repository orders by the time-ordered UUIDv7 id, so the deterministic page order is id-ascending.
        var ordered = expected.OrderBy(id => id).ToArray();
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var route = $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}";

        // First page of two: entries 0,1, more remain.
        var first = await client.GetFromJsonAsync<PageDto<SessionItem>>($"{route}&limit=2&offset=0", _json);
        Assert.NotNull(first);
        Assert.Equal(2, first.Limit);
        Assert.Equal(0, first.Offset);
        Assert.True(first.HasMore);
        Assert.Equal(new[] { ordered[0], ordered[1] }, first.Items.Select(s => s.Id).ToArray());

        // Middle page of two: entries 2,3, more remain.
        var second = await client.GetFromJsonAsync<PageDto<SessionItem>>($"{route}&limit=2&offset=2", _json);
        Assert.NotNull(second);
        Assert.True(second.HasMore);
        Assert.Equal(new[] { ordered[2], ordered[3] }, second.Items.Select(s => s.Id).ToArray());

        // Last page: the single remaining entry 4, no more.
        var last = await client.GetFromJsonAsync<PageDto<SessionItem>>($"{route}&limit=2&offset=4", _json);
        Assert.NotNull(last);
        Assert.False(last.HasMore);
        var only = Assert.Single(last.Items);
        Assert.Equal(ordered[4], only.Id);

        // The pages stitch back to the full set with no gaps or overlaps.
        var stitched = first.Items.Concat(second.Items).Concat(last.Items).Select(s => s.Id).ToArray();
        Assert.Equal(ordered, stitched);
    }

    // ---- OVER-LARGE WORKSPACE: the default page does not return everything ----

    [Fact]
    public async Task An_over_large_workspace_does_not_return_an_unbounded_entity_payload()
    {
        // A workspace with MORE entities than the default page size: the unpaged list must NOT return them all
        // as an unbounded array — it caps at the default page size and reports hasMore.
        var overLarge = Page.DefaultLimit + 10;
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var type = await db.AddEntityTypeAsync(org.Id, workspace.Id, "type-alpha");
            for (var i = 0; i < overLarge; i++)
            {
                await db.AddEntityAsync(org.Id, workspace.Id, type.Id, $"Entity {i}");
            }
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/entities?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<EntityItem>>(_json);
        Assert.NotNull(page);
        // The default page is capped at the default size, NOT the over-large total, and reports more remain.
        Assert.Equal(Page.DefaultLimit, page.Limit);
        Assert.Equal(Page.DefaultLimit, page.Items.Count);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Scene_list_pages_the_role_projected_shape_and_bounds_an_over_large_workspace()
    {
        // The role-projected scene list still returns a bounded page envelope (the items are role-projected,
        // the envelope bounds the SET). An over-large workspace is capped at the default page size.
        var overLarge = Page.DefaultLimit + 3;
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            for (var i = 0; i < overLarge; i++)
            {
                await db.AddSceneAsync(org.Id, workspace.Id, $"Scene {i}", i);
            }
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/scenes?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PageDto<SceneItem>>(_json);
        Assert.NotNull(page);
        Assert.Equal(Page.DefaultLimit, page.Limit);
        Assert.Equal(Page.DefaultLimit, page.Items.Count);
        Assert.True(page.HasMore);
        // The scenes come back in their deterministic (scene_order) order.
        Assert.Equal(0, page.Items[0].Order);
        Assert.Equal(Page.DefaultLimit - 1, page.Items[^1].Order);
    }

    // ---- MALFORMED paging parameters -> 400 (after authorization) ------------

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-3")]
    [InlineData("limit=abc")]
    [InlineData("offset=-1")]
    [InlineData("offset=notanumber")]
    public async Task Session_list_with_a_malformed_paging_parameter_is_400(string pagingQuery)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            workspaceId = workspace.Id;
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}&{pagingQuery}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_paging_parameter_is_not_leaked_to_a_non_member()
    {
        // Request-shape validation runs AFTER authorization (mirrors the audit read): a non-member of the
        // workspace gets the hidden 404, even with a malformed limit (the 400 only ever reaches an authorized
        // member).
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        Guid workspaceId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            workspaceId = workspace.Id;
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{workspaceId}/sessions?organizationSlug={_orgA}&limit=0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed record SessionItem(Guid Id, string Title, string Status);

    private sealed record EntityItem(Guid Id, string Name);

    private sealed record SceneItem(Guid Id, string Title, int Order);
}
