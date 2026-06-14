using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Sessions;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Content;

/// <summary>
/// Integration-style tests for the <see cref="ContentBlockDeletionService"/> (CORE-LIFE-004, the "Resource
/// Lifecycle and Deletion" epic), the command behind
/// <c>DELETE /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation and the explicit cleanup of
/// the POLYMORPHIC (non-FK) <c>visibility_rules</c> and <c>asset_links</c> are exercised on every run without
/// a database server.
///
/// Coverage (the story's "visibility rules and revisions are cleaned up consistently; authorized", with the
/// dependents handled consistently with CORE-LIFE-003):
/// <list type="bullet">
///   <item>CASCADE: deleting a content block removes the content block (and its inline revision history),
///   every visibility rule governing it (the audience-wide rule AND a selected-participant rule) and every
///   asset link targeting it — while the LINKED ASSET, the SCENE and an unrelated content block's own
///   rule/link all SURVIVE (cascade, not over-reach).</item>
///   <item>AUDIT: a successful deletion appends exactly one append-only
///   <see cref="AuditAction.ContentBlockDeleted"/> record naming the actor and the deleted content block, and
///   no state pair.</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling scene (same workspace),
///   an id in a sibling workspace and an id in another tenant all return
///   <see cref="ContentBlockDeletionResult.NotFound"/> and change NOTHING (the sibling/foreign content block
///   and its dependents survive, and no audit record is written).</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary
/// appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class ContentBlockDeletionServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _deleteTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public ContentBlockDeletionServiceTests()
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
    private static ContentBlockDeletionService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new ContentBlockRepository(context),
            new VisibilityRuleRepository(context),
            new AssetLinkRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Delete_removes_the_content_block_and_cascades_all_dependents_while_unrelated_rows_survive()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneId, seeded.BlockA, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(ContentBlockDeletionResult.Deleted, result);
        }

        await using var verify = CreateContext();

        // The deleted content block is gone; its dependents are gone.
        Assert.False(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockA));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.AudienceRuleA));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.ParticipantRuleA));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.LinkToA));

        // The scene, the linked ASSET, and the unrelated content block's own rule/link all survive — the
        // cascade removed only what depended on BlockA.
        Assert.True(await verify.Scenes.AsNoTracking().AnyAsync(s => s.Id == seeded.SceneId));
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockB));
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == seeded.AssetId));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.UnrelatedRuleB));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == seeded.UnrelatedLinkToB));
    }

    [Fact]
    public async Task Delete_appends_one_content_block_deleted_audit_record_for_the_actor()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneId, seeded.BlockA, seeded.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.ContentBlockDeleted)
            .ToListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(seeded.OrganizationId, entry.OrganizationId);
        Assert.Equal(seeded.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(seeded.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(ContentBlock), entry.ResourceType);
        Assert.Equal(seeded.BlockA, entry.ResourceId);
        // A deletion is a removal, not a transition: no before/after state pair.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_content_block_and_writes_no_audit()
    {
        var seeded = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.SceneId, Guid.CreateVersion7(), seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(ContentBlockDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        // Nothing was deleted, nothing was audited.
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockA));
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.ContentBlockDeleted));
    }

    [Fact]
    public async Task Delete_through_a_sibling_scene_is_not_found_and_keeps_the_content_block()
    {
        // BlockA lives in scene S; addressing it through sibling scene S2 of the SAME workspace must never
        // reach it (scene-scoped lookup; threat T1/T5).
        var seeded = await SeedFullGraphAsync();
        Guid siblingSceneId;
        await using (var seed = CreateContext())
        {
            var sibling = Scene.Create(seeded.OrganizationId, seeded.WorkspaceId, "Sibling Scene", 2, _seedTime);
            seed.Scenes.Add(sibling);
            await seed.SaveChangesAsync();
            siblingSceneId = sibling.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                seeded.OrganizationId, seeded.WorkspaceId, siblingSceneId, seeded.BlockA, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(ContentBlockDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockA));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.AudienceRuleA));
    }

    [Fact]
    public async Task Delete_through_a_sibling_workspace_is_not_found_and_keeps_the_content_block()
    {
        // BlockA lives in workspace W; addressing it through sibling workspace W2 of the SAME org must never
        // reach it (workspace-scoped lookup; threat T1/T5).
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
                seeded.OrganizationId, siblingWorkspaceId, seeded.SceneId, seeded.BlockA, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(ContentBlockDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockA));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == seeded.AudienceRuleA));
    }

    [Fact]
    public async Task Delete_through_a_foreign_tenant_is_not_found_and_keeps_the_content_block()
    {
        // BlockA lives in org A's workspace; addressing it with org B's id must never reach it
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
                orgBId, seeded.WorkspaceId, seeded.SceneId, seeded.BlockA, seeded.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(ContentBlockDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.ContentBlocks.AsNoTracking().AnyAsync(c => c.Id == seeded.BlockA));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public async Task Delete_rejects_empty_required_ids(bool org, bool workspace, bool scene, bool block, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            scene ? Guid.Empty : Guid.CreateVersion7(),
            block ? Guid.Empty : Guid.CreateVersion7(),
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _deleteTime,
            CancellationToken.None));
    }

    /// <summary>
    /// Seeds one organization + workspace + scene holding: the actor user; BlockA (to delete) with an
    /// audience-wide and a selected-participant visibility rule and an asset link from a real asset; plus an
    /// UNRELATED BlockB with its own rule and link that must survive BlockA's deletion. Returns the ids the
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

        var scene = Scene.Create(org.Id, workspace.Id, "Opening Scene", 1, _seedTime);
        context.Scenes.Add(scene);
        await context.SaveChangesAsync();

        var blockA = ContentBlock.Create(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "block-a body", _seedTime);
        var blockB = ContentBlock.Create(org.Id, workspace.Id, scene.Id, ContentBlockType.Text, "block-b body", _seedTime);
        context.ContentBlocks.AddRange(blockA, blockB);
        await context.SaveChangesAsync();

        var participant = Participant.Create(org.Id, workspace.Id, actor.Id, "Participant", _seedTime);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();

        var session = Session.Create(org.Id, workspace.Id, "Live Session", _seedTime);
        context.Sessions.Add(session);
        await context.SaveChangesAsync();

        // BlockA dependents.
        var audienceRuleA = VisibilityRule.Create(
            org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockA.Id, VisibilityState.Visible, _seedTime);
        var participantRuleA = VisibilityRule.CreateForParticipant(
            org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockA.Id, participant.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.AddRange(audienceRuleA, participantRuleA);

        var asset = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}", "image/png", _seedTime);
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        var linkToA = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, blockA.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(linkToA);

        // UNRELATED BlockB dependents (must survive BlockA's deletion).
        var unrelatedRuleB = VisibilityRule.Create(
            org.Id, workspace.Id, session.Id, VisibilityResourceType.ContentBlock, blockB.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.Add(unrelatedRuleB);
        var unrelatedLinkToB = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.ContentBlock, blockB.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(unrelatedLinkToB);

        await context.SaveChangesAsync();

        return new SeededGraph
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            SceneId = scene.Id,
            Actor = actor.Id,
            BlockA = blockA.Id,
            BlockB = blockB.Id,
            AssetId = asset.Id,
            AudienceRuleA = audienceRuleA.Id,
            ParticipantRuleA = participantRuleA.Id,
            LinkToA = linkToA.Id,
            UnrelatedRuleB = unrelatedRuleB.Id,
            UnrelatedLinkToB = unrelatedLinkToB.Id,
        };
    }

    private sealed class SeededGraph
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid SceneId { get; init; }
        public Guid Actor { get; init; }
        public Guid BlockA { get; init; }
        public Guid BlockB { get; init; }
        public Guid AssetId { get; init; }
        public Guid AudienceRuleA { get; init; }
        public Guid ParticipantRuleA { get; init; }
        public Guid LinkToA { get; init; }
        public Guid UnrelatedRuleB { get; init; }
        public Guid UnrelatedLinkToB { get; init; }
    }
}
