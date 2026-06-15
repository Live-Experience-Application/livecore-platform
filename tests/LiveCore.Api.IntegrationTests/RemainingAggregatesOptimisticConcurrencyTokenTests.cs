using LiveCore.Api.Assets;
using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Integration coverage for the optimistic-concurrency tokens that CORE-CONC-006 added
/// to the REMAINING mutable aggregates — the eight that CORE-CONC-001 left doing a bare
/// Update+SaveChanges with no token and silent last-write-wins. It exercises the
/// headline acceptance scenarios named by the story (Scene.Reorder, Entity and Asset):
/// two interleaved read-modify-write commands on ONE row where the SECOND write must be
/// rejected (a conflict) instead of silently overwriting the first, plus the
/// single-writer happy path being unaffected — directly against the database through two
/// independent <see cref="LiveCoreDbContext"/> instances, exactly the cross-context race
/// the in-memory aggregate guards cannot catch.
///
/// <para>
/// The conflict is detected by the PostgreSQL <c>xmin</c> row-version token, a
/// PostgreSQL system column; the default in-memory SQLite test provider has no such
/// column, so the token is mapped ONLY on Npgsql (see <see cref="LiveCoreDbContext"/>).
/// These tests therefore assert the conflict only when the suite runs against PostgreSQL
/// (the CI <c>integration-postgres</c> job, <see cref="PostgresTestDatabase.IsConfigured"/>);
/// on the SQLite path they instead assert the same interleaving does NOT throw — proving
/// the provider-conditional mapping leaves the SQLite path fully working — and that the
/// single-writer happy path succeeds on both providers. This mirrors the
/// <see cref="OptimisticConcurrencyTokenTests"/> coverage of the original six aggregates;
/// the HTTP translation of the resulting <see cref="DbUpdateConcurrencyException"/> into a
/// 409 is covered by the <c>ConcurrencyConflictMiddleware</c> unit tests.
/// </para>
/// </summary>
public sealed class RemainingAggregatesOptimisticConcurrencyTokenTests
{
    private static readonly DateTimeOffset _commandTime = TestData.SeedTime.AddMinutes(5);

    [Fact]
    public async Task Interleaved_scene_reorders_make_the_second_writer_conflict()
    {
        await using var factory = new WorkspaceApiFactory();

        // Creating the client builds the host and provisions the test database.
        using var client = factory.CreateClient();

        Guid sceneId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var organization = await context.AddOrganizationAsync("concurrency-org");
            var workspace = await context.AddWorkspaceAsync(organization.Id, "concurrency-ws", "Concurrency Workspace");
            var scene = await context.AddSceneAsync(organization.Id, workspace.Id, "Race Scene", order: 0);
            sceneId = scene.Id;
        });

        // Two writers each load the SAME scene into their own context, so both capture
        // the same row version before either commits — the classic interleaved
        // read-modify-write Scene.Reorder runs (used by the deletion re-pack).
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var firstScene = await firstContext.Scenes.SingleAsync(scene => scene.Id == sceneId);
        var secondScene = await secondContext.Scenes.SingleAsync(scene => scene.Id == sceneId);

        // The first writer reorders the scene and commits; its write wins and the row
        // version advances.
        firstScene.Reorder(3, _commandTime);
        await firstContext.SaveChangesAsync();

        // The second writer holds the now-stale snapshot and reorders the same scene to
        // a different position. Only the persistence token can stop the second write
        // from silently overwriting the first.
        secondScene.Reorder(7, _commandTime);

        if (PostgresTestDatabase.IsConfigured)
        {
            // The stale xmin no longer matches, so the second SaveChanges fails loudly
            // instead of losing the first writer's reorder.
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());

            using var verifyScope = factory.Services.CreateScope();
            var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
            var scene = await verifyContext.Scenes.SingleAsync(s => s.Id == sceneId);
            Assert.Equal(3, scene.Order);
        }
        else
        {
            // SQLite carries no xmin token, so the interleaving is last-write-wins and
            // does not throw — confirming the Npgsql-only mapping does not break the
            // default test provider.
            await secondContext.SaveChangesAsync();
        }

        // Whatever the provider, the single-writer happy path is unaffected: a fresh
        // reader can reorder the scene and commit successfully.
        using var happyScope = factory.Services.CreateScope();
        var happyContext = happyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var happyScene = await happyContext.Scenes.SingleAsync(scene => scene.Id == sceneId);
        happyScene.Reorder(5, _commandTime);
        await happyContext.SaveChangesAsync();

        using var finalScope = factory.Services.CreateScope();
        var finalContext = finalScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var finalScene = await finalContext.Scenes.SingleAsync(scene => scene.Id == sceneId);
        Assert.Equal(5, finalScene.Order);
    }

    [Fact]
    public async Task Interleaved_entity_renames_make_the_second_writer_conflict()
    {
        await using var factory = new WorkspaceApiFactory();

        using var client = factory.CreateClient();

        Guid entityId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var organization = await context.AddOrganizationAsync("concurrency-org");
            var workspace = await context.AddWorkspaceAsync(organization.Id, "concurrency-ws", "Concurrency Workspace");
            var entityType = await context.AddEntityTypeAsync(organization.Id, workspace.Id);
            var entity = await context.AddEntityAsync(organization.Id, workspace.Id, entityType.Id, "Original");
            entityId = entity.Id;
        });

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var firstEntity = await firstContext.Entities.SingleAsync(entity => entity.Id == entityId);
        var secondEntity = await secondContext.Entities.SingleAsync(entity => entity.Id == entityId);

        // The first rename commits first and wins.
        firstEntity.Rename("First Winner", _commandTime);
        await firstContext.SaveChangesAsync();

        // The second rename raced on the stale snapshot.
        secondEntity.Rename("Second Loser", _commandTime);

        if (PostgresTestDatabase.IsConfigured)
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());

            // The conflict means the second rename did NOT silently overwrite the first:
            // the entity is left with the first writer's committed name.
            using var verifyScope = factory.Services.CreateScope();
            var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
            var entity = await verifyContext.Entities.SingleAsync(e => e.Id == entityId);
            Assert.Equal("First Winner", entity.Name);
        }
        else
        {
            await secondContext.SaveChangesAsync();
        }

        // Single-writer happy path: a fresh reader can rename the entity and commit.
        using var happyScope = factory.Services.CreateScope();
        var happyContext = happyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var happyEntity = await happyContext.Entities.SingleAsync(entity => entity.Id == entityId);
        happyEntity.Rename("Final", _commandTime);
        await happyContext.SaveChangesAsync();

        using var finalScope = factory.Services.CreateScope();
        var finalContext = finalScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var finalEntity = await finalContext.Entities.SingleAsync(entity => entity.Id == entityId);
        Assert.Equal("Final", finalEntity.Name);
    }

    [Fact]
    public async Task Interleaved_asset_confirmations_make_the_second_writer_conflict()
    {
        await using var factory = new WorkspaceApiFactory();

        using var client = factory.CreateClient();

        Guid assetId = Guid.Empty;
        await factory.SeedAsync(async context =>
        {
            var organization = await context.AddOrganizationAsync("concurrency-org");
            var workspace = await context.AddWorkspaceAsync(organization.Id, "concurrency-ws", "Concurrency Workspace");
            var user = await context.AddUserAsync("https://issuer.test", "subject-asset");
            // A still-Pending asset, so both writers can drive the confirm transition.
            var asset = await context.AddAssetAsync(organization.Id, workspace.Id, user.Id, available: false);
            assetId = asset.Id;
        });

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var firstContext = firstScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();

        var firstAsset = await firstContext.Assets.SingleAsync(asset => asset.Id == assetId);
        var secondAsset = await secondContext.Assets.SingleAsync(asset => asset.Id == assetId);

        // The first confirmation commits first and wins (the asset becomes Available).
        firstAsset.MarkAvailable(
            4096, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", _commandTime);
        await firstContext.SaveChangesAsync();

        // The second confirmation raced on the stale snapshot.
        secondAsset.MarkAvailable(
            8192, "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752", _commandTime);

        if (PostgresTestDatabase.IsConfigured)
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());

            // The conflict means the second confirmation did NOT silently overwrite the
            // first: the asset keeps the first writer's recorded size.
            using var verifyScope = factory.Services.CreateScope();
            var verifyContext = verifyScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
            var asset = await verifyContext.Assets.SingleAsync(a => a.Id == assetId);
            Assert.Equal(AssetStatus.Available, asset.Status);
            Assert.Equal(4096L, asset.SizeBytes);
        }
        else
        {
            await secondContext.SaveChangesAsync();
        }

        // Whatever the provider, the single-writer happy path is unaffected: a fresh
        // reader sees an Available asset.
        using var verifyAvailableScope = factory.Services.CreateScope();
        var verifyAvailableContext = verifyAvailableScope.ServiceProvider.GetRequiredService<LiveCoreDbContext>();
        var availableAsset = await verifyAvailableContext.Assets.SingleAsync(asset => asset.Id == assetId);
        Assert.Equal(AssetStatus.Available, availableAsset.Status);
    }
}
