using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Entities;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Organizations;
using LiveCore.Api.Participants;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Integration-style tests for the <see cref="EntityDeletionService"/> (CORE-LIFE-003, the "Resource
/// Lifecycle and Deletion" epic), the command behind
/// <c>DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}</c>.
///
/// They run against an in-memory SQLite database with foreign keys enforced
/// (<c>PRAGMA foreign_keys = ON</c>), so the real model mapping, SQL translation, the FK cascade on
/// <c>entity_relationships</c> and the explicit cleanup of the POLYMORPHIC (non-FK)
/// <c>visibility_rules</c> and <c>asset_links</c> are exercised on every run without a database server.
///
/// Coverage (the story's "delete entity, dependent edges/rules resolved" plus negative scope):
/// <list type="bullet">
///   <item>CASCADE: deleting an entity removes the entity, every directed edge touching it (both
///   directions), every visibility rule governing it (the audience-wide rule AND a selected-participant
///   rule) and every asset link targeting it — while the LINKED ASSET, the OTHER endpoint entity and an
///   unrelated entity's own edge/rule/link all SURVIVE (cascade, not over-reach).</item>
///   <item>AUDIT: a successful deletion appends exactly one append-only <see cref="AuditAction.EntityDeleted"/>
///   record naming the actor and the deleted entity, and no state pair.</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling workspace, and an id
///   in another tenant all return <see cref="EntityDeletionResult.NotFound"/> and change NOTHING (the
///   sibling/foreign entity and its dependents survive, and no audit record is written).</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names/kinds are GENERIC and NEUTRAL — no vertical vocabulary
/// appears (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class EntityDeletionServiceTests : IDisposable
{
    private const string _issuer = "https://issuer.test";
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _deleteTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public EntityDeletionServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _contextOptions = new DbContextOptionsBuilder<LiveCoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new LiveCoreDbContext(_contextOptions);
        context.Database.EnsureCreated();
        // Enforce foreign keys so the entity_relationships cascade and the (org/workspace/asset/user) FKs
        // are genuinely exercised.
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
    private static EntityDeletionService CreateService(LiveCoreDbContext context)
        => new(
            new TransactionalUnitOfWork(context),
            new EntityRepository(context),
            new EntityRelationshipRepository(context),
            new VisibilityRuleRepository(context),
            new AssetLinkRepository(context),
            new AuditLogRepository(context));

    [Fact]
    public async Task Delete_removes_the_entity_and_cascades_all_dependents_while_unrelated_rows_survive()
    {
        var scene = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                scene.OrganizationId, scene.WorkspaceId, scene.EntityA, scene.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(EntityDeletionResult.Deleted, result);
        }

        await using var verify = CreateContext();

        // The deleted entity is gone; its dependents are gone.
        Assert.False(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityA));
        Assert.False(await verify.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == scene.EdgeAToB));
        Assert.False(await verify.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == scene.EdgeBToA));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == scene.AudienceRuleA));
        Assert.False(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == scene.ParticipantRuleA));
        Assert.False(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == scene.LinkToA));

        // The OTHER endpoint entity, the linked ASSET, and the unrelated entity's own edge/rule/link all
        // survive — the cascade removed only what depended on EntityA.
        Assert.True(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityB));
        Assert.True(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityC));
        Assert.True(await verify.Assets.AsNoTracking().AnyAsync(a => a.Id == scene.AssetId));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == scene.UnrelatedRuleC));
        Assert.True(await verify.AssetLinks.AsNoTracking().AnyAsync(l => l.Id == scene.UnrelatedLinkToC));
        Assert.True(await verify.EntityRelationships.AsNoTracking().AnyAsync(r => r.Id == scene.UnrelatedEdgeC));
    }

    [Fact]
    public async Task Delete_appends_one_entity_deleted_audit_record_for_the_actor()
    {
        var scene = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.DeleteAsync(
                scene.OrganizationId, scene.WorkspaceId, scene.EntityA, scene.Actor, _deleteTime, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var entries = await verify.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditAction.EntityDeleted)
            .ToListAsync();

        var entry = Assert.Single(entries);
        Assert.Equal(scene.OrganizationId, entry.OrganizationId);
        Assert.Equal(scene.WorkspaceId, entry.WorkspaceId);
        Assert.Equal(scene.Actor, entry.ActorUserProfileId);
        Assert.Equal(nameof(Entity), entry.ResourceType);
        Assert.Equal(scene.EntityA, entry.ResourceId);
        // A deletion is a removal, not a transition: no before/after state pair.
        Assert.Null(entry.PreviousState);
        Assert.Null(entry.NewState);
    }

    [Fact]
    public async Task Delete_returns_not_found_for_an_unknown_entity_and_writes_no_audit()
    {
        var scene = await SeedFullGraphAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                scene.OrganizationId, scene.WorkspaceId, Guid.CreateVersion7(), scene.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(EntityDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        // Nothing was deleted, nothing was audited.
        Assert.True(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityA));
        Assert.False(await verify.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditAction.EntityDeleted));
    }

    [Fact]
    public async Task Delete_through_a_sibling_workspace_is_not_found_and_keeps_the_entity()
    {
        // EntityA lives in workspace W; addressing it through sibling workspace W2 of the SAME org must
        // never reach it (workspace-scoped lookup; threat T1/T5).
        var scene = await SeedFullGraphAsync();
        Guid siblingWorkspaceId;
        await using (var seed = CreateContext())
        {
            var sibling = Workspace.Create(scene.OrganizationId, "sibling-show", "Sibling Show", _seedTime);
            seed.Workspaces.Add(sibling);
            await seed.SaveChangesAsync();
            siblingWorkspaceId = sibling.Id;
        }

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.DeleteAsync(
                scene.OrganizationId, siblingWorkspaceId, scene.EntityA, scene.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(EntityDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityA));
        Assert.True(await verify.VisibilityRules.AsNoTracking().AnyAsync(v => v.Id == scene.AudienceRuleA));
    }

    [Fact]
    public async Task Delete_through_a_foreign_tenant_is_not_found_and_keeps_the_entity()
    {
        // EntityA lives in org A's workspace; addressing it with org B's id must never reach it
        // (organization boundary checked before workspace boundary; threat T5).
        var scene = await SeedFullGraphAsync();
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
                orgBId, scene.WorkspaceId, scene.EntityA, scene.Actor, _deleteTime, CancellationToken.None);
            Assert.Equal(EntityDeletionResult.NotFound, result);
        }

        await using var verify = CreateContext();
        Assert.True(await verify.Entities.AsNoTracking().AnyAsync(e => e.Id == scene.EntityA));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public async Task Delete_rejects_empty_required_ids(bool org, bool workspace, bool entity, bool actor)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            entity ? Guid.Empty : Guid.CreateVersion7(),
            actor ? Guid.Empty : Guid.CreateVersion7(),
            _deleteTime,
            CancellationToken.None));
    }

    /// <summary>
    /// Seeds one organization + workspace holding: the actor user; EntityA (to delete) with an
    /// audience-wide and a selected-participant visibility rule, an asset link from a real asset, and two
    /// directed edges (A-&gt;B and B-&gt;A) to EntityB; plus an UNRELATED EntityC with its own rule, link
    /// and self-contained edge that must survive EntityA's deletion. Returns the ids the tests assert on.
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

        var type = EntityType.Create(org.Id, workspace.Id, "type-alpha", "Type Alpha", "{}", _seedTime);
        context.EntityTypes.Add(type);
        await context.SaveChangesAsync();

        var entityA = Entity.Create(org.Id, workspace.Id, type.Id, "entity-a", "{}", _seedTime);
        var entityB = Entity.Create(org.Id, workspace.Id, type.Id, "entity-b", "{}", _seedTime);
        var entityC = Entity.Create(org.Id, workspace.Id, type.Id, "entity-c", "{}", _seedTime);
        context.Entities.AddRange(entityA, entityB, entityC);
        await context.SaveChangesAsync();

        var participant = Participant.Create(org.Id, workspace.Id, actor.Id, "Participant", _seedTime);
        context.Participants.Add(participant);
        await context.SaveChangesAsync();

        // EntityA dependents.
        var audienceRuleA = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.Entity, entityA.Id, VisibilityState.Visible, _seedTime);
        var participantRuleA = VisibilityRule.CreateForParticipant(
            org.Id, workspace.Id, VisibilityResourceType.Entity, entityA.Id, participant.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.AddRange(audienceRuleA, participantRuleA);

        var edgeAToB = EntityRelationship.Create(org.Id, workspace.Id, entityA.Id, entityB.Id, "links-to", _seedTime);
        var edgeBToA = EntityRelationship.Create(org.Id, workspace.Id, entityB.Id, entityA.Id, "links-to", _seedTime);
        context.EntityRelationships.AddRange(edgeAToB, edgeBToA);

        var asset = Asset.Create(
            org.Id, workspace.Id, actor.Id, "s3", "livecore-private-assets",
            $"assets/{org.Id}/{workspace.Id}/{Guid.CreateVersion7()}", "image/png", _seedTime);
        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        var linkToA = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entityA.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(linkToA);

        // UNRELATED EntityC dependents (must survive EntityA's deletion).
        var unrelatedRuleC = VisibilityRule.Create(
            org.Id, workspace.Id, VisibilityResourceType.Entity, entityC.Id, VisibilityState.Visible, _seedTime);
        context.VisibilityRules.Add(unrelatedRuleC);
        var unrelatedLinkToC = AssetLink.Create(
            org.Id, workspace.Id, asset.Id, AssetLinkTargetType.Entity, entityC.Id, actor.Id, _seedTime);
        context.AssetLinks.Add(unrelatedLinkToC);
        // An edge between B and C, untouched by deleting A.
        var unrelatedEdgeC = EntityRelationship.Create(org.Id, workspace.Id, entityB.Id, entityC.Id, "links-to", _seedTime);
        context.EntityRelationships.Add(unrelatedEdgeC);

        await context.SaveChangesAsync();

        return new SeededGraph
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Actor = actor.Id,
            EntityA = entityA.Id,
            EntityB = entityB.Id,
            EntityC = entityC.Id,
            AssetId = asset.Id,
            AudienceRuleA = audienceRuleA.Id,
            ParticipantRuleA = participantRuleA.Id,
            EdgeAToB = edgeAToB.Id,
            EdgeBToA = edgeBToA.Id,
            LinkToA = linkToA.Id,
            UnrelatedRuleC = unrelatedRuleC.Id,
            UnrelatedLinkToC = unrelatedLinkToC.Id,
            UnrelatedEdgeC = unrelatedEdgeC.Id,
        };
    }

    private sealed class SeededGraph
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Actor { get; init; }
        public Guid EntityA { get; init; }
        public Guid EntityB { get; init; }
        public Guid EntityC { get; init; }
        public Guid AssetId { get; init; }
        public Guid AudienceRuleA { get; init; }
        public Guid ParticipantRuleA { get; init; }
        public Guid EdgeAToB { get; init; }
        public Guid EdgeBToA { get; init; }
        public Guid LinkToA { get; init; }
        public Guid UnrelatedRuleC { get; init; }
        public Guid UnrelatedLinkToC { get; init; }
        public Guid UnrelatedEdgeC { get; init; }
    }
}
