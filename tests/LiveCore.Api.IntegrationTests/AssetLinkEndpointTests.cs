using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the asset-linking flow (CORE-AST-005,
/// <c>POST /api/v1/assets/{assetId}/links</c>). They drive the real application over real HTTP through
/// <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON), so the
/// documented request flow (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; inline
/// object-level authorization -&gt; command) is exercised end-to-end. Linking never touches the object
/// storage, so no fake <see cref="IAssetStorage"/> is needed.
///
/// Coverage, per the story's required tests ("Upload/download auth tests and storage adapter tests") and
/// the threat model (threat T5 tenant/workspace isolation; threat T1 broken object-level authorization;
/// threat T4 "Asset leak" — the link is what later makes an asset audience-accessible):
/// <list type="bullet">
///   <item>HAPPY PATH: a host (and each link role) links an asset to a content block / entity in the
///   asset's workspace -&gt; 201, and exactly one link is persisted.</item>
///   <item>AUTHORIZATION + ISOLATION: 401 unauthenticated; the non-link roles {Participant, Observer,
///   Auditor} -&gt; 403 with NO link; a non-member of the asset's workspace, an asset in another tenant, an
///   unknown asset and a malformed asset id are ALL hidden as 404.</item>
///   <item>SAME-WORKSPACE COUPLING: a target content block in ANOTHER workspace and an unknown target id
///   are hidden as 404 with NO link (an asset can never be linked to a foreign-workspace resource).</item>
///   <item>VALIDATION: a missing organizationSlug, an unknown/numeric/Scene target type and an empty target
///   id are 400.</item>
///   <item>DUPLICATE: re-linking the same asset to the same target is 409 and creates no second link.</item>
/// </list>
/// Every denial/validation case asserts NO link was created. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AssetLinkEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static TheoryData<MembershipRole> LinkRoles =>
    [
        MembershipRole.Owner,
        MembershipRole.Admin,
        MembershipRole.Host,
        MembershipRole.CoHost,
    ];

    public static TheoryData<MembershipRole> NonLinkRoles =>
    [
        MembershipRole.Participant,
        MembershipRole.Observer,
        MembershipRole.Auditor,
    ];

    [Fact]
    public async Task Link_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await PostAsync(client, Guid.CreateVersion7(), Body(_orgA, "ContentBlock", Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_links_an_asset_to_a_content_block()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LinkDto>(_json);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.LinkId);
        Assert.Equal(seed.AssetId, body.AssetId);
        Assert.Equal(nameof(AssetLinkTargetType.ContentBlock), body.TargetType);
        Assert.Equal(seed.ContentBlockId, body.TargetId);

        var links = await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId);
        var link = Assert.Single(links);
        Assert.Equal(body.LinkId, link.Id);
        Assert.Equal(AssetLinkTargetType.ContentBlock, link.TargetType);
        Assert.Equal(seed.ContentBlockId, link.TargetId);
        Assert.Equal(await SingleUserProfileIdAsync(factory), link.CreatedByUserProfileId);
    }

    [Fact]
    public async Task Host_links_an_asset_to_an_entity()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "Entity", seed.EntityId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = Assert.Single(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
        Assert.Equal(AssetLinkTargetType.Entity, link.TargetType);
        Assert.Equal(seed.EntityId, link.TargetId);
    }

    [Theory]
    [MemberData(nameof(LinkRoles))]
    public async Task Link_is_201_for_a_link_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Theory]
    [MemberData(nameof(NonLinkRoles))]
    public async Task Link_is_403_for_a_non_link_workspace_role_and_creates_no_link(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Link_is_404_for_an_org_member_who_is_not_a_member_of_the_assets_workspace()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "outsider-a";
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var insider = await db.AddUserAsync(_issuer, "insider-a");
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, caller.Id, MembershipRole.Owner);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            // The caller is NOT a member of the workspace (only the insider is).
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, insider.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, insider.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            seed = new SeedResult(org.Id, workspace.Id, asset.Id, block.Id, Guid.Empty);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Link_is_404_for_an_asset_in_another_tenant()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "user-a";
        SeedResult seedB = default;
        await factory.SeedAsync(async db =>
        {
            var caller = await db.AddUserAsync(_issuer, subject);
            var creatorInB = await db.AddUserAsync(_issuer, "creator-b");
            var orgA = await db.AddOrganizationAsync(_orgA);
            var orgB = await db.AddOrganizationAsync(_orgB);
            await db.AddOrganizationMemberAsync(orgA.Id, caller.Id, MembershipRole.Owner);
            var workspaceInB = await db.AddWorkspaceAsync(orgB.Id, "b-show", "B Show");
            await db.AddWorkspaceMemberAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, MembershipRole.Host);
            var assetInB = await db.AddAssetAsync(orgB.Id, workspaceInB.Id, creatorInB.Id, available: true);
            var scene = await db.AddSceneAsync(orgB.Id, workspaceInB.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(orgB.Id, workspaceInB.Id, scene.Id, ContentBlockType.Text, "Generic");
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, assetInB.Id, block.Id, Guid.Empty);
        });

        // Address the org-B asset with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seedB.AssetId, Body(_orgA, "ContentBlock", seedB.ContentBlockId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seedB.OrganizationId, seedB.WorkspaceId, seedB.AssetId));
    }

    [Fact]
    public async Task Link_is_404_for_an_unknown_asset()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, Guid.CreateVersion7(), Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Link_is_404_for_a_malformed_asset_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.PostAsync(
            "/api/v1/assets/not-a-guid/links", JsonContent.Create(Body(_orgA, "ContentBlock", seed.ContentBlockId), options: _json));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Link_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(null, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Scene")]
    [InlineData("bogus")]
    [InlineData("1")]
    public async Task Link_is_400_for_an_invalid_target_type(string targetType)
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, targetType, seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Link_is_400_for_an_empty_target_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", Guid.Empty));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Link_is_404_for_a_target_not_in_the_assets_workspace()
    {
        // A content block that EXISTS but in another workspace is not a valid target (same-workspace
        // coupling; threats T5/T1): hidden as 404, no link created.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        SeedResult seed = default;
        Guid foreignBlockId = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspaceA = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspaceA.Id, user.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspaceA.Id, user.Id, available: true);
            // A content block in a DIFFERENT workspace of the same tenant.
            var workspaceB = await db.AddWorkspaceAsync(org.Id, "winter-show", "Winter Show");
            var sceneB = await db.AddSceneAsync(org.Id, workspaceB.Id, "Scene", 1);
            var blockInB = await db.AddContentBlockAsync(org.Id, workspaceB.Id, sceneB.Id, ContentBlockType.Text, "Generic");
            foreignBlockId = blockInB.Id;
            seed = new SeedResult(org.Id, workspaceA.Id, asset.Id, Guid.Empty, Guid.Empty);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", foreignBlockId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Link_is_404_for_an_unknown_target_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await PostAsync(client, seed.AssetId, Body(_orgA, "Entity", Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    [Fact]
    public async Task Re_linking_the_same_asset_to_the_same_target_is_409()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var first = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostAsync(client, seed.AssetId, Body(_orgA, "ContentBlock", seed.ContentBlockId));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        // Still exactly one link.
        Assert.Single(await LinksAsync(factory, seed.OrganizationId, seed.WorkspaceId, seed.AssetId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static object Body(string? organizationSlug, string? targetType, Guid targetId)
        => new { organizationSlug, targetType, targetId };

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid assetId, object body)
        => await client.PostAsync($"/api/v1/assets/{assetId}/links", JsonContent.Create(body, options: _json));

    /// <summary>
    /// Seeds an org + a caller with the given role in both the org and a workspace (all in org A) plus an
    /// asset, a content block and an entity in that workspace.
    /// </summary>
    private static async Task<SeedResult> SeedAsync(WorkspaceApiFactory factory, string subject, MembershipRole role)
    {
        SeedResult seed = default;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, role);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, role);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            var entityType = await db.AddEntityTypeAsync(org.Id, workspace.Id);
            var entity = await db.AddEntityAsync(org.Id, workspace.Id, entityType.Id);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id, block.Id, entity.Id);
        });
        return seed;
    }

    private static async Task<IReadOnlyList<AssetLink>> LinksAsync(
        WorkspaceApiFactory factory, Guid organizationId, Guid workspaceId, Guid assetId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AssetLinks.AsNoTracking()
            .Where(link => link.OrganizationId == organizationId
                && link.WorkspaceId == workspaceId
                && link.AssetId == assetId)
            .OrderBy(link => link.Id)
            .ToListAsync();
    }

    private static async Task<Guid> SingleUserProfileIdAsync(WorkspaceApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.UserProfiles.AsNoTracking().Select(profile => profile.Id).SingleAsync();
    }

    private readonly record struct SeedResult(
        Guid OrganizationId, Guid WorkspaceId, Guid AssetId, Guid ContentBlockId, Guid EntityId);

    private sealed record LinkDto(Guid LinkId, Guid AssetId, string TargetType, Guid TargetId, DateTimeOffset CreatedAt);
}
