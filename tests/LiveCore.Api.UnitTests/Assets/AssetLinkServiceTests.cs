// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using LiveCore.Api.Content;
using LiveCore.Api.Entities;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Tests for <see cref="AssetLinkService"/> (CORE-AST-005) — the create-link command. The service is the
/// place the SAME-WORKSPACE COUPLING for the polymorphic target reference is enforced: it resolves the
/// target content block / entity through the workspace-scoped repository of the asset's OWN organization
/// and workspace before linking. The tests run against an in-memory SQLite database with foreign keys
/// enforced and the real content-block / entity / asset-link repositories, so the cross-aggregate
/// existence check and the per-workspace uniqueness are genuinely exercised.
///
/// Coverage (the epic acceptance "assets are private by default and accessed only through authorized signed
/// URLs"; threat T5 tenant/workspace isolation; threat T1 broken object-level authorization):
/// <list type="bullet">
///   <item>Linking to a content block / entity IN the asset's workspace succeeds and persists the
///   link.</item>
///   <item>Linking to a target in ANOTHER workspace, or to a non-existent id, is reported as
///   <see cref="AssetLinkOutcome.TargetNotFound"/> and persists NO link (an asset can never be linked to a
///   foreign-workspace resource).</item>
///   <item>Re-linking the same asset to the same target is reported as
///   <see cref="AssetLinkOutcome.AlreadyLinked"/> and persists no duplicate.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class AssetLinkServiceTests : IDisposable
{
    private const string _issuer = "https://id.example.test/realms/livecore";

    private const string _organizationSlugA = "northwind-labs";
    private const string _workspaceSlugA = "summer-show";
    private const string _workspaceSlugB = "winter-show";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public AssetLinkServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    private AssetLinkService CreateService(LiveCoreDbContext context)
        => new(new AssetLinkRepository(context), new ContentBlockRepository(context), new EntityRepository(context));

    private async Task<Organization> SeedOrganizationAsync(string slug)
    {
        var organization = Organization.Create(slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(OrganizationAddResult.Added, await new OrganizationRepository(context).AddAsync(organization, CancellationToken.None));
        return organization;
    }

    private async Task<Workspace> SeedWorkspaceAsync(Guid organizationId, string slug)
    {
        var workspace = Workspace.Create(organizationId, slug, slug, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(WorkspaceAddResult.Added, await new WorkspaceRepository(context).AddAsync(workspace, CancellationToken.None));
        return workspace;
    }

    private async Task<UserProfile> SeedUserAsync()
    {
        var profile = UserProfile.CreateFromPrincipal(new OidcPrincipal(PrincipalType.User, _issuer, Guid.CreateVersion7().ToString()), _createdAt);
        await using var context = CreateContext();
        Assert.Equal(UserProfileAddResult.Added, await new UserProfileRepository(context).AddAsync(profile, CancellationToken.None));
        return profile;
    }

    private async Task<Asset> SeedAssetAsync(Guid organizationId, Guid workspaceId, Guid createdBy)
    {
        var asset = Asset.Create(
            organizationId, workspaceId, createdBy, "s3", "livecore-private-assets",
            $"assets/{organizationId}/{workspaceId}/{Guid.CreateVersion7()}", "image/png", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetAddResult.Added, await new AssetRepository(context).AddAsync(asset, CancellationToken.None));
        return asset;
    }

    private async Task<ContentBlock> SeedContentBlockAsync(Guid organizationId, Guid workspaceId)
    {
        var scene = Scene.Create(organizationId, workspaceId, "Scene", 1, _createdAt);
        await using (var sceneContext = CreateContext())
        {
            sceneContext.Scenes.Add(scene);
            await sceneContext.SaveChangesAsync(CancellationToken.None);
        }

        var block = ContentBlock.Create(organizationId, workspaceId, scene.Id, ContentBlockType.Text, "Generic content", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(ContentBlockAddResult.Added, await new ContentBlockRepository(context).AddAsync(block, CancellationToken.None));
        return block;
    }

    private async Task<Entity> SeedEntityAsync(Guid organizationId, Guid workspaceId)
    {
        var entityType = EntityType.Create(organizationId, workspaceId, "generic-type", "Generic Type", "{}", _createdAt);
        await using (var typeContext = CreateContext())
        {
            typeContext.EntityTypes.Add(entityType);
            await typeContext.SaveChangesAsync(CancellationToken.None);
        }

        var entity = Entity.Create(organizationId, workspaceId, entityType.Id, "Generic Entity", "{}", _createdAt);
        await using var context = CreateContext();
        Assert.Equal(EntityAddResult.Added, await new EntityRepository(context).AddAsync(entity, CancellationToken.None));
        return entity;
    }

    private async Task<int> LinkCountAsync()
    {
        await using var context = CreateContext();
        return await context.AssetLinks.CountAsync(CancellationToken.None);
    }

    private async Task<AssetLink> SeedLinkAsync(
        Guid organizationId, Guid workspaceId, Guid assetId, AssetLinkTargetType targetType, Guid targetId, Guid createdBy)
    {
        var link = AssetLink.Create(organizationId, workspaceId, assetId, targetType, targetId, createdBy, _createdAt);
        await using var context = CreateContext();
        Assert.Equal(AssetLinkAddResult.Added, await new AssetLinkRepository(context).AddAsync(link, CancellationToken.None));
        return link;
    }

    [Fact]
    public async Task Linking_to_a_content_block_in_the_assets_workspace_succeeds()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var block = await SeedContentBlockAsync(org.Id, ws.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).LinkAsync(
            asset, AssetLinkTargetType.ContentBlock, block.Id, user.Id, _createdAt, CancellationToken.None);

        Assert.Equal(AssetLinkOutcome.Linked, result.Outcome);
        Assert.NotNull(result.Link);
        Assert.Equal(asset.Id, result.Link.AssetId);
        Assert.Equal(block.Id, result.Link.TargetId);
        Assert.Equal(1, await LinkCountAsync());
    }

    [Fact]
    public async Task Linking_to_an_entity_in_the_assets_workspace_succeeds()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var entity = await SeedEntityAsync(org.Id, ws.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).LinkAsync(
            asset, AssetLinkTargetType.Entity, entity.Id, user.Id, _createdAt, CancellationToken.None);

        Assert.Equal(AssetLinkOutcome.Linked, result.Outcome);
        Assert.Equal(1, await LinkCountAsync());
    }

    [Fact]
    public async Task Linking_to_a_non_existent_target_reports_target_not_found_and_persists_nothing()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).LinkAsync(
            asset, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id, _createdAt, CancellationToken.None);

        Assert.Equal(AssetLinkOutcome.TargetNotFound, result.Outcome);
        Assert.Null(result.Link);
        Assert.Equal(0, await LinkCountAsync());
    }

    [Fact]
    public async Task Linking_to_a_content_block_in_another_workspace_reports_target_not_found()
    {
        // The crown jewel of the same-workspace coupling (threats T5/T1): a content block that exists, but
        // in a DIFFERENT workspace than the asset, is not a valid target — no link is created.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var wsA = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var wsB = await SeedWorkspaceAsync(org.Id, _workspaceSlugB);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, wsA.Id, user.Id);
        // The content block lives in workspace B, but the asset is in workspace A.
        var blockInB = await SeedContentBlockAsync(org.Id, wsB.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).LinkAsync(
            asset, AssetLinkTargetType.ContentBlock, blockInB.Id, user.Id, _createdAt, CancellationToken.None);

        Assert.Equal(AssetLinkOutcome.TargetNotFound, result.Outcome);
        Assert.Equal(0, await LinkCountAsync());
    }

    [Fact]
    public async Task Relinking_the_same_asset_to_the_same_target_reports_already_linked()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var entity = await SeedEntityAsync(org.Id, ws.Id);

        await using (var firstContext = CreateContext())
        {
            var first = await CreateService(firstContext).LinkAsync(
                asset, AssetLinkTargetType.Entity, entity.Id, user.Id, _createdAt, CancellationToken.None);
            Assert.Equal(AssetLinkOutcome.Linked, first.Outcome);
        }

        await using var context = CreateContext();
        var second = await CreateService(context).LinkAsync(
            asset, AssetLinkTargetType.Entity, entity.Id, user.Id, _createdAt, CancellationToken.None);

        Assert.Equal(AssetLinkOutcome.AlreadyLinked, second.Outcome);
        Assert.Null(second.Link);
        // Still exactly one link.
        Assert.Equal(1, await LinkCountAsync());
    }

    [Fact]
    public async Task Unlinking_an_existing_link_removes_only_that_link()
    {
        // CORE-LIFE-007: the inverse command removes exactly the addressed link. Another link of the same
        // asset survives — removal only detaches one target.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var toRemove = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);
        var survivor = await SeedLinkAsync(org.Id, ws.Id, asset.Id, AssetLinkTargetType.ContentBlock, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).UnlinkAsync(asset, toRemove.Id, CancellationToken.None);

        Assert.Equal(AssetUnlinkResult.Removed, result);
        Assert.Equal(1, await LinkCountAsync());
        await using var verify = CreateContext();
        var repository = new AssetLinkRepository(verify);
        Assert.Null(await repository.FindByIdAsync(org.Id, ws.Id, toRemove.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindByIdAsync(org.Id, ws.Id, survivor.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Unlinking_a_link_that_attaches_a_different_asset_reports_not_found()
    {
        // The crown jewel of the asset/link pairing (threats T5/T1): a link whose id resolves in the
        // workspace but attaches a DIFFERENT asset is not addressable through this asset's route — it is
        // reported as not found and nothing is removed.
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var assetA = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var assetB = await SeedAssetAsync(org.Id, ws.Id, user.Id);
        var linkOnB = await SeedLinkAsync(org.Id, ws.Id, assetB.Id, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).UnlinkAsync(assetA, linkOnB.Id, CancellationToken.None);

        Assert.Equal(AssetUnlinkResult.NotFound, result);
        // Asset B's link is untouched.
        Assert.Equal(1, await LinkCountAsync());
        await using var verify = CreateContext();
        Assert.NotNull(await new AssetLinkRepository(verify).FindByIdAsync(org.Id, ws.Id, linkOnB.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Unlinking_an_unknown_link_reports_not_found()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);

        await using var context = CreateContext();
        var result = await CreateService(context).UnlinkAsync(asset, Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(AssetUnlinkResult.NotFound, result);
        Assert.Equal(0, await LinkCountAsync());
    }

    [Fact]
    public async Task UnlinkAsync_rejects_invalid_arguments()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);

        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.UnlinkAsync(null!, Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UnlinkAsync(asset, Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task LinkAsync_rejects_invalid_arguments()
    {
        var org = await SeedOrganizationAsync(_organizationSlugA);
        var ws = await SeedWorkspaceAsync(org.Id, _workspaceSlugA);
        var user = await SeedUserAsync();
        var asset = await SeedAssetAsync(org.Id, ws.Id, user.Id);

        await using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.LinkAsync(
            null!, AssetLinkTargetType.Entity, Guid.CreateVersion7(), user.Id, _createdAt, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.LinkAsync(
            asset, (AssetLinkTargetType)999, Guid.CreateVersion7(), user.Id, _createdAt, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(
            asset, AssetLinkTargetType.Entity, Guid.Empty, user.Id, _createdAt, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LinkAsync(
            asset, AssetLinkTargetType.Entity, Guid.CreateVersion7(), Guid.Empty, _createdAt, CancellationToken.None));
    }
}
