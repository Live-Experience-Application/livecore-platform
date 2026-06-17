// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Net;
using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// HTTP integration tests for the asset-link removal flow (CORE-LIFE-007,
/// <c>DELETE /api/v1/assets/{assetId}/links/{linkId}</c>). They drive the real application over real HTTP
/// through <see cref="WorkspaceApiFactory"/> (test authentication scheme + EF Core SQLite, foreign keys ON),
/// so the documented request flow (authentication -&gt; tenant context resolver -&gt; endpoint -&gt; inline
/// object-level authorization -&gt; command) is exercised end-to-end. Unlinking never touches the object
/// storage, so no fake <see cref="IAssetStorage"/> is needed.
///
/// Coverage, per the story's required tests (Unit + integration + authorization negative tests) and the
/// acceptance criterion ("a host can unlink an asset from a content block or entity; the asset and target are
/// unaffected; authorized"):
/// <list type="bullet">
///   <item>HAPPY PATH: a host (and each link role) unlinks an asset from a content block -&gt; 204, the link
///   is gone, and BOTH the asset and the target content block are unaffected.</item>
///   <item>AUTHORIZATION + ISOLATION (threat T5 tenant/workspace isolation; threat T1 broken object-level
///   authorization): 401 unauthenticated; the non-link roles {Participant, Observer, Auditor} -&gt; 403 with
///   the link kept; a non-member of the asset's workspace, an asset in another tenant, an unknown/malformed
///   asset id, an unknown/malformed link id, and a link that attaches a DIFFERENT asset are ALL hidden as 404
///   with the link kept where one exists.</item>
///   <item>VALIDATION: a missing organizationSlug is 400 and keeps the link.</item>
/// </list>
/// Every denial/validation case asserts the link was NOT removed. All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class AssetLinkRemovalEndpointTests
{
    private const string _issuer = "https://issuer.test";
    private const string _orgA = "northwind-labs";
    private const string _orgB = "acme-co";

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
    public async Task Unlink_unauthenticated_is_401()
    {
        await using var factory = new WorkspaceApiFactory();
        using var client = factory.CreateAnonymousClient();

        var response = await DeleteAsync(client, Guid.CreateVersion7(), Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_unlinks_an_asset_and_the_asset_and_target_are_unaffected()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, seed.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The link row is gone...
        Assert.False(await LinkExistsAsync(factory, seed.LinkId));
        // ...but the asset and the target content block are BOTH unaffected (the acceptance criterion).
        Assert.True(await AssetExistsAsync(factory, seed.AssetId));
        Assert.True(await ContentBlockExistsAsync(factory, seed.ContentBlockId));
    }

    [Theory]
    [MemberData(nameof(LinkRoles))]
    public async Task Unlink_is_204_for_a_link_workspace_role(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, seed.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Theory]
    [MemberData(nameof(NonLinkRoles))]
    public async Task Unlink_is_403_for_a_non_link_workspace_role_and_keeps_the_link(MembershipRole role)
    {
        await using var factory = new WorkspaceApiFactory();
        var subject = $"member-{role}";
        var seed = await SeedAsync(factory, subject, role);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, seed.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_an_org_member_who_is_not_a_member_of_the_assets_workspace()
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
            var link = await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, insider.Id);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id, block.Id, link.Id);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, seed.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_an_asset_in_another_tenant()
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
            var link = await db.AddAssetLinkAsync(orgB.Id, workspaceInB.Id, assetInB.Id, AssetLinkTargetType.ContentBlock, block.Id, creatorInB.Id);
            seedB = new SeedResult(orgB.Id, workspaceInB.Id, assetInB.Id, block.Id, link.Id);
        });

        // Address the org-B asset/link with organizationSlug = A (the caller's own org): hidden as 404.
        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seedB.AssetId, seedB.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seedB.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_an_unknown_asset()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, Guid.CreateVersion7(), seed.LinkId, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The link still exists (the unknown asset never reached the link).
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_a_malformed_asset_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/not-a-guid/links/{seed.LinkId}?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_a_malformed_link_id()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await client.DeleteAsync($"/api/v1/assets/{seed.AssetId}/links/not-a-guid?organizationSlug={_orgA}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_an_unknown_link()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, Guid.CreateVersion7(), _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // The real link is untouched.
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    [Fact]
    public async Task Unlink_is_404_for_a_link_that_attaches_a_different_asset_and_keeps_the_link()
    {
        // A link id that resolves in the workspace but attaches a DIFFERENT asset is not addressable through
        // this asset's route (the asset/link pairing; threats T5/T1): hidden as 404, the link kept.
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        SeedResult seed = default;
        Guid otherAssetId = Guid.Empty;
        Guid linkOnOther = Guid.Empty;
        await factory.SeedAsync(async db =>
        {
            var user = await db.AddUserAsync(_issuer, subject);
            var org = await db.AddOrganizationAsync(_orgA);
            await db.AddOrganizationMemberAsync(org.Id, user.Id, MembershipRole.Host);
            var workspace = await db.AddWorkspaceAsync(org.Id, "summer-show", "Summer Show");
            await db.AddWorkspaceMemberAsync(org.Id, workspace.Id, user.Id, MembershipRole.Host);
            var asset = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var other = await db.AddAssetAsync(org.Id, workspace.Id, user.Id, available: true);
            var scene = await db.AddSceneAsync(org.Id, workspace.Id, "Scene", 1);
            var block = await db.AddContentBlockAsync(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "Generic");
            // The link attaches the OTHER asset, not the addressed one.
            var link = await db.AddAssetLinkAsync(org.Id, workspace.Id, other.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            otherAssetId = other.Id;
            linkOnOther = link.Id;
            seed = new SeedResult(org.Id, workspace.Id, asset.Id, block.Id, Guid.Empty);
        });

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        // Address the link via the WRONG asset (seed.AssetId), though linkOnOther attaches otherAssetId.
        var response = await DeleteAsync(client, seed.AssetId, linkOnOther, _orgA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, linkOnOther));
        Assert.True(await AssetExistsAsync(factory, otherAssetId));
    }

    [Fact]
    public async Task Unlink_is_400_without_the_organization_slug()
    {
        await using var factory = new WorkspaceApiFactory();
        const string subject = "host-a";
        var seed = await SeedAsync(factory, subject, MembershipRole.Host);

        using var client = factory.CreateClientFor(subject, _issuer, _orgA);
        var response = await DeleteAsync(client, seed.AssetId, seed.LinkId, organizationSlug: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await LinkExistsAsync(factory, seed.LinkId));
    }

    // =====================================================================
    // Helpers.
    // =====================================================================

    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient client, Guid assetId, Guid linkId, string? organizationSlug)
    {
        var url = $"/api/v1/assets/{assetId}/links/{linkId}";
        if (organizationSlug is not null)
        {
            url += $"?organizationSlug={organizationSlug}";
        }

        return await client.DeleteAsync(url);
    }

    /// <summary>
    /// Seeds an org + a caller with the given role in both the org and a workspace (all in org A) plus an
    /// asset, a content block in that workspace and a link attaching the asset to the content block.
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
            var link = await db.AddAssetLinkAsync(org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, block.Id, user.Id);
            seed = new SeedResult(org.Id, workspace.Id, asset.Id, block.Id, link.Id);
        });
        return seed;
    }

    private static async Task<bool> LinkExistsAsync(WorkspaceApiFactory factory, Guid linkId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.AssetLinks.AsNoTracking().AnyAsync(link => link.Id == linkId);
    }

    private static async Task<bool> AssetExistsAsync(WorkspaceApiFactory factory, Guid assetId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.Assets.AsNoTracking().AnyAsync(asset => asset.Id == assetId);
    }

    private static async Task<bool> ContentBlockExistsAsync(WorkspaceApiFactory factory, Guid contentBlockId)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        return await context.ContentBlocks.AsNoTracking().AnyAsync(block => block.Id == contentBlockId);
    }

    private readonly record struct SeedResult(
        Guid OrganizationId, Guid WorkspaceId, Guid AssetId, Guid ContentBlockId, Guid LinkId);
}
