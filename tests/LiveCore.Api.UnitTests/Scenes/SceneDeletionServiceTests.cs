using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Scenes;

/// <summary>
/// Integration-style tests for the <see cref="SceneDeletionService"/> (CORE-LIFE-005, the "Resource
/// Lifecycle and Deletion" epic), the command behind
/// <c>DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId}</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the explicit cleanup of
/// the POLYMORPHIC (non-FK) <c>visibility_rules</c> and <c>asset_links</c>, and the order re-pack are
/// exercised on every run without a database server.
///
/// Coverage (the story's "remaining scenes re-pack their ordering without gaps; child content blocks are
/// removed; authorized"):
/// <list type="bullet">
///   <item>CASCADE: deleting a scene removes the scene, its OWN visibility rules (audience-wide AND
///   selected-participant), every CHILD content block (and that block's own visibility rules and asset
///   links) — while the LINKED ASSET, the SURVIVING scenes, a surviving scene's own rule, and an unrelated
///   content block's rule/link all SURVIVE (cascade, not over-reach).</item>
///   <item>RE-PACK: after the deletion the remaining scenes are re-numbered to a contiguous, gap-free
///   <c>scene_order</c> in their existing relative order — whether the deleted scene was the first, a middle
///   or the last scene.</item>
///   <item>AUDIT: a successful deletion appends exactly one append-only <see cref="AuditAction.SceneDeleted"/>
///   record naming the actor and the deleted scene, and no state pair.</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling workspace and an id in
///   another tenant all return <see cref="SceneDeletionResult.NotFound"/> and change NOTHING (the
///   sibling/foreign scene, its dependents and the existing orders survive, and no audit record is
///   written).</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary
/// appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class SceneDeletionServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _deleteTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SceneDeletionServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // Enforce foreign keys so the (org/workspace/scene/asset/user) FKs are genuinely exercised.
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
    }

    public void Dispose() => _connection.Dispose();

    private LiveCoreDbContext CreateContext()
    {
        var context = new LiveCoreDbContext(_contextOptions);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON;");
        return context;
    }

    // The service and every repository it composes MUST share one context instance so the explicit
    // transaction enrols each repository's SaveChanges.
    private static SceneDeletionService CreateService(LiveCoreDbContext context)
        => new(
            context,
            new SceneRepository(context),
            new ContentBlockRepository(context),
            new VisibilityRuleRepository(context),
            new AssetLinkRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Delete_removes_the_scene_and_cascades_children_and_rules_while_unrelated_rows_survive()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(SceneDeletionResult.Deleted, result);
        }

        await using var verify = CreateContext();

        // The deleted scene is gone, with its own visibility rules.
        Assert.False(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.SceneToDelete));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.SceneAudienceRule));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.SceneParticipantRule));

        // Its child content blocks are gone, with their own visibility rules and asset links.
        Assert.False(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.ChildBlock1));
        Assert.False(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.ChildBlock2));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.Child1AudienceRule));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.Child1ParticipantRule));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.Child2AudienceRule));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Child1Link));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.Child2Link));

        // The surviving scenes, a surviving scene's OWN visibility rule, the unrelated content block's
        // rule/link and the linked ASSET all survive — the cascade removed only what depended on the deleted
        // scene.
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.Scene0));
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.Scene2));
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.Scene3));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.Scene3OwnRule));
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.UnrelatedBlock));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.UnrelatedBlockRule));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.UnrelatedBlockLink));
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetId));
    }

    [Fact]
    public async Task Delete_repacks_the_remaining_scene_ordering_without_gaps()
    {
        // Seed orders 0,1,2,3; delete the MIDDLE scene (order 1). The survivors (orders 0,2,3) must re-pack
        // to a contiguous 0,1,2 in their existing relative order.
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var orderById = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToDictionaryAsync(s => s.Id, s => s.Order);

        // Three scenes remain, re-packed to a contiguous, gap-free 0,1,2 (relative order preserved).
        Assert.Equal(3, orderById.Count);
        Assert.Equal(0, orderById[seeded.Scene0]);
        Assert.Equal(1, orderById[seeded.Scene2]);
        Assert.Equal(2, orderById[seeded.Scene3]);
    }

    [Fact]
    public async Task Delete_first_scene_repacks_survivors_down_by_one()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene0, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var orderById = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToDictionaryAsync(s => s.Id, s => s.Order);

        // The deleted first scene leaves the others; they re-pack to 0,1,2.
        Assert.Equal(3, orderById.Count);
        Assert.Equal(0, orderById[seeded.SceneToDelete]);
        Assert.Equal(1, orderById[seeded.Scene2]);
        Assert.Equal(2, orderById[seeded.Scene3]);
    }

    [Fact]
    public async Task Delete_last_scene_leaves_the_other_orders_unchanged()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene3, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var orderById = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToDictionaryAsync(s => s.Id, s => s.Order);

        // Deleting the last (highest-order) scene leaves a contiguous 0,1,2 — no gap was created.
        Assert.Equal(3, orderById.Count);
        Assert.Equal(0, orderById[seeded.Scene0]);
        Assert.Equal(1, orderById[seeded.SceneToDelete]);
        Assert.Equal(2, orderById[seeded.Scene2]);
    }

    [Fact]
    public async Task Delete_appends_one_scene_deleted_audit_record_for_the_actor()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.SceneDeleted)
            .ToListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(seeded.OrganizationId, entry.OrganizationId);
        Assert.Equal(seeded.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seeded.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(Scene), entry.ResourceType);
        Assert.Equal(seeded.SceneToDelete, entry.ResourceId);
        // A deletion is a removal, not a transition: no before/after state pair.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_scene_and_writes_no_audit_and_keeps_orders()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, Guid.CreateVersion7(), seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(SceneDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        // Nothing was deleted, nothing was audited, no orders re-packed.
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.SceneToDelete));
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.SceneDeleted));
        var orderById = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToDictionaryAsync(s => s.Id, s => s.Order);
        Assert.Equal(0, orderById[seeded.Scene0]);
        Assert.Equal(1, orderById[seeded.SceneToDelete]);
        Assert.Equal(2, orderById[seeded.Scene2]);
        Assert.Equal(3, orderById[seeded.Scene3]);
    }

    [Fact]
    public async Task Delete_through_a_sibling_workspace_is_not_found_and_keeps_the_scene()
    {
        // The scene lives in workspace W; addressing it through sibling workspace W2 of the SAME org must
        // never reach it (workspace-scoped lookup; threat T1/T5).
        var seeded = await SeedFullGraphAsync();
        Guid siblingWorkspaceId;
        await using (var seed = CreateContext())
        {
            var sibling = Workspace.Create(seeded.OrganizationId, "sibling-show", "Sibling Show", _seedTime);
            seed.Workspaces.Add(sibling);
            await seed.SaveChangesAsync();
            siblingWorkspaceId = sibling.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, siblingWorkspaceId, seeded.SceneToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(SceneDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.SceneToDelete));
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.ChildBlock1));
    }

    [Fact]
    public async Task Delete_through_a_foreign_tenant_is_not_found_and_keeps_the_scene()
    {
        // The scene lives in org A's workspace; addressing it with org B's id must never reach it
        // (organization boundary checked before workspace boundary; threat T5).
        var seeded = await SeedFullGraphAsync();
        Guid orgBId;
        await using (var seed = CreateContext())
        {
            var orgB = Organization.Create(_orgSlugB, _orgSlugB, _seedTime);
            seed.Organizations.Add(orgB);
            await seed.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                orgBId, seeded.WorkspaceId, seeded.SceneToDelete, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(SceneDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.SceneToDelete));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Delete_rejects_empty_required_ids(bool org, bool workspace, bool scene, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            scene ? Guid.Empty : Guid.CreateVersion7(),
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _deleteTime,
            CancellationToken.None));
    }

    /// <summary>
    /// Seeds one organization + workspace holding four scenes at contiguous orders 0..3: the actor user;
    /// SceneToDelete (order 1) carrying its own audience-wide and selected-participant visibility rules plus
    /// two child content blocks (each with their own rule(s) and an asset link from a real asset); the
    /// neighbouring Scene0 (order 0), Scene2 (order 2) and Scene3 (order 3, which carries its OWN visibility
    /// rule and an UNRELATED content block with a rule and link that must all survive). Returns the ids the
    /// tests assert on.
    /// </summary>
    private async Task<SeededGraph> SeedFullGraphAsync()
    {
        await using var context = CreateContext();

        var actor = UserProfile.CreateFromPrincipal(
            new OidcPrincipal(PrincipalType.User, _issuer, "host-a"),
            _seedTime);
        context.UserProfiles.Add(actor);

        var org = Organization.Create(_orgSlugA, _orgSlugA, _seedTime);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        var workspace = Workspace.Create(org.Id, "summer-show", "Summer Show", _seedTime);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var scene0 = Scene.Create(org.Id, workspace.Id, "Scene Zero", 0, _seedTime);
        var sceneToDelete = Scene.Create(org.Id, workspace.Id, "Scene To Delete", 1, _seedTime);
        var scene2 = Scene.Create(org.Id, workspace.Id, "Scene Two", 2, _seedTime);
        var scene3 = Scene.Create(org.Id, workspace.Id, "Scene Three", 3, _seedTime);
        context.Scenes.AddRange(scene0, sceneToDelete, scene2, scene3);
        await context.SaveChangesAsync();

        var participant = Participant.Create(org.Id, workspace.Id, actor.Id, "Participant", _seedTime);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();

        // Child content blocks of the scene to delete.
        var child1 = ContentBlock.Create(org.Id, workspace.Id, sceneToDelete.Id, ContentBlockType.Text, "child-1 body", _seedTime);
        var child2 = ContentBlock.Create(org.Id, workspace.Id, sceneToDelete.Id, ContentBlockType.Text, "child-2 body", _seedTime);
        // An unrelated content block in a SURVIVING scene that must NOT be touched.
        var unrelatedBlock = ContentBlock.Create(org.Id, workspace.Id, scene2.Id, ContentBlockType.Text, "unrelated body", _seedTime);
        context.ContentBlocks.AddRange(child1, child2, unrelatedBlock);
        await context.SaveChangesAsync();

        var asset = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}", "image/png", _seedTime);
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        // The scene's OWN visibility rules (resource_type = Scene).
        var sceneAudienceRule = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.Scene, sceneToDelete.Id, VisibilityState.Visible, _seedTime);
        var sceneParticipantRule = VisibilityRule.CreateForParticipant(
            org.Id, workspace.Id, VisibilityResourceType.Scene, sceneToDelete.Id, participant.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.AddRange(sceneAudienceRule, sceneParticipantRule);

        // Child block 1 dependents.
        var child1AudienceRule = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.ContentBlock, child1.Id, VisibilityState.Visible, _seedTime);
        var child1ParticipantRule = VisibilityRule.CreateForParticipant(
            org.Id, workspace.Id, VisibilityResourceType.ContentBlock, child1.Id, participant.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.AddRange(child1AudienceRule, child1ParticipantRule);
        var child1Link = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, child1.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(child1Link);

        // Child block 2 dependents.
        var child2AudienceRule = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.ContentBlock, child2.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.Add(child2AudienceRule);
        var child2Link = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, child2.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(child2Link);

        // SURVIVING scene 3's own rule, and an UNRELATED block's rule + link (all must survive).
        var scene3OwnRule = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.Scene, scene3.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.Add(scene3OwnRule);
        var unrelatedBlockRule = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.ContentBlock, unrelatedBlock.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.Add(unrelatedBlockRule);
        var unrelatedBlockLink = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, unrelatedBlock.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(unrelatedBlockLink);

        await context.SaveChangesAsync();

        return new SeededGraph
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
            AssetId = asset.Id,
            Scene0 = scene0.Id,
            SceneToDelete = sceneToDelete.Id,
            Scene2 = scene2.Id,
            Scene3 = scene3.Id,
            SceneAudienceRule = sceneAudienceRule.Id,
            SceneParticipantRule = sceneParticipantRule.Id,
            ChildBlock1 = child1.Id,
            ChildBlock2 = child2.Id,
            Child1AudienceRule = child1AudienceRule.Id,
            Child1ParticipantRule = child1ParticipantRule.Id,
            Child2AudienceRule = child2AudienceRule.Id,
            Child1Link = child1Link.Id,
            Child2Link = child2Link.Id,
            Scene3OwnRule = scene3OwnRule.Id,
            UnrelatedBlock = unrelatedBlock.Id,
            UnrelatedBlockRule = unrelatedBlockRule.Id,
            UnrelatedBlockLink = unrelatedBlockLink.Id,
        };
    }

    private sealed class SeededGraph
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
        public Guid AssetId { get; init; }
        public Guid Scene0 { get; init; }
        public Guid SceneToDelete { get; init; }
        public Guid Scene2 { get; init; }
        public Guid Scene3 { get; init; }
        public Guid SceneAudienceRule { get; init; }
        public Guid SceneParticipantRule { get; init; }
        public Guid ChildBlock1 { get; init; }
        public Guid ChildBlock2 { get; init; }
        public Guid Child1AudienceRule { get; init; }
        public Guid Child1ParticipantRule { get; init; }
        public Guid Child2AudienceRule { get; init; }
        public Guid Child1Link { get; init; }
        public Guid Child2Link { get; init; }
        public Guid Scene3OwnRule { get; init; }
        public Guid UnrelatedBlock { get; init; }
        public Guid UnrelatedBlockRule { get; init; }
        public Guid UnrelatedBlockLink { get; init; }
    }
}
