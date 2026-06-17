// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;
using LiveCore.Api.Persistence;
using LiveCore.Api.Scenes;
using LiveCore.Api.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.UnitTests.Scenes;

/// <summary>
/// Integration-style tests for the <see cref="SceneReorderService"/> (CORE-SCENE-006, the "Authoring
/// Completeness" epic), the command behind
/// <c>POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder</c>.
///
/// They run against an in-memory SQLite database, so the real model mapping, the SQL translation and the
/// order re-pack are exercised on every run without a database server.
///
/// Coverage (the story's "a host can move a scene to a new position; the workspace scene ordering re-packs
/// deterministically with no gaps or duplicates; server-assigned order"):
/// <list type="bullet">
///   <item>MOVE + RE-PACK: moving a scene forward, backward, to the front and to the back each yields the
///   expected contiguous, gap-free (scene_order, id) sequence with the other scenes' relative order
///   preserved.</item>
///   <item>SERVER-ASSIGNED / CLAMP: a target index at or beyond the end moves the scene to the last slot (the
///   index is a request, not a trusted absolute order).</item>
///   <item>NO-OP: moving a scene to its current position writes nothing and leaves every order (and update
///   timestamp) untouched.</item>
///   <item>NOT FOUND / ISOLATION (threats T1/T5): an unknown id, an id in a sibling workspace and an id in
///   another tenant all return <see cref="SceneReorderResult.NotFound"/> and change NOTHING.</item>
///   <item>GUARDS: empty required ids and a negative target index are rejected.</item>
/// </list>
///
/// THE TEMPLATE BOUNDARY (docs/04): all names are GENERIC and NEUTRAL — no vertical vocabulary appears
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public sealed class SceneReorderServiceTests : IDisposable
{
    private const string _orgSlugA = "northwind-labs";
    private const string _orgSlugB = "acme-co";

    private static readonly DateTimeOffset _seedTime = new(2026, 6, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _reorderTime = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LiveCoreDbContext> _contextOptions;

    public SceneReorderServiceTests()
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

    // The service and the repository it composes MUST share one context instance so the explicit transaction
    // enrols the repository's SaveChanges.
    private static SceneReorderService CreateService(LiveCoreDbContext context)
        => new(new TransactionalUnitOfWork(context), new SceneRepository(context));

    [Fact]
    public async Task Reorder_moving_a_scene_forward_yields_the_expected_sequence()
    {
        // Move Scene0 (order 0) to index 2 -> [S1, S2, S0, S3].
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene0, 2, _reorderTime, CancellationToken.None);
            Assert.Equal(SceneReorderResult.Reordered, result);
        }

        await AssertSequenceAsync(seeded, seeded.Scene1, seeded.Scene2, seeded.Scene0, seeded.Scene3);
    }

    [Fact]
    public async Task Reorder_moving_a_scene_backward_yields_the_expected_sequence()
    {
        // Move Scene3 (order 3) to index 1 -> [S0, S3, S1, S2].
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene3, 1, _reorderTime, CancellationToken.None);
        }

        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene3, seeded.Scene1, seeded.Scene2);
    }

    [Fact]
    public async Task Reorder_to_the_front_yields_the_expected_sequence()
    {
        // Move Scene2 (order 2) to index 0 -> [S2, S0, S1, S3].
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene2, 0, _reorderTime, CancellationToken.None);
        }

        await AssertSequenceAsync(seeded, seeded.Scene2, seeded.Scene0, seeded.Scene1, seeded.Scene3);
    }

    [Fact]
    public async Task Reorder_to_a_too_large_index_clamps_to_the_end()
    {
        // Move Scene0 to index 99 -> clamped to the last slot -> [S1, S2, S3, S0].
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene0, 99, _reorderTime, CancellationToken.None);
        }

        await AssertSequenceAsync(seeded, seeded.Scene1, seeded.Scene2, seeded.Scene3, seeded.Scene0);
    }

    [Fact]
    public async Task Reorder_to_the_same_position_is_a_no_op_and_writes_nothing()
    {
        // Move Scene1 (order 1) to index 1 -> unchanged, and no row is re-written (update timestamps stay at
        // the seed time).
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene1, 1, _reorderTime, CancellationToken.None);
            Assert.Equal(SceneReorderResult.Reordered, result);
        }

        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene1, seeded.Scene2, seeded.Scene3);

        await using var verify = CreateContext();
        // No scene was re-written, so every update timestamp is still the seed time.
        var scenes = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToListAsync();
        Assert.All(scenes, s => Assert.Equal(_seedTime, s.UpdatedAt));
    }

    [Fact]
    public async Task Reorder_only_rewrites_the_scenes_whose_order_changed()
    {
        // Move Scene2 (order 2) to index 3 -> [S0, S1, S3, S2]. Only S3 (2<-3) and S2 (3<-2) shift; S0 and S1
        // keep order 0 and 1 and so are NOT re-written (their update timestamps stay at the seed time).
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, seeded.Scene2, 3, _reorderTime, CancellationToken.None);
        }

        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene1, seeded.Scene3, seeded.Scene2);

        await using var verify = CreateContext();
        var byId = await verify.Scenes.AsNoTracking()
            .Where(s => s.WorkspaceId == seeded.WorkspaceId)
            .ToDictionaryAsync(s => s.Id, s => s.UpdatedAt);
        Assert.Equal(_seedTime, byId[seeded.Scene0]);
        Assert.Equal(_seedTime, byId[seeded.Scene1]);
        Assert.Equal(_reorderTime, byId[seeded.Scene2]);
        Assert.Equal(_reorderTime, byId[seeded.Scene3]);
    }

    [Fact]
    public async Task Reorder_returns_not_found_for_an_unknown_scene_and_keeps_the_orders()
    {
        var seeded = await SeedWorkspaceAsync();

        await using (var context = CreateContext())
        {
            var service = CreateService(context);
            var result = await service.ReorderAsync(
                seeded.OrganizationId, seeded.WorkspaceId, Guid.CreateVersion7(), 0, _reorderTime, CancellationToken.None);
            Assert.Equal(SceneReorderResult.NotFound, result);
        }

        // Nothing moved.
        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene1, seeded.Scene2, seeded.Scene3);
    }

    [Fact]
    public async Task Reorder_through_a_sibling_workspace_is_not_found_and_keeps_the_orders()
    {
        // The scene lives in workspace W; addressing it through sibling workspace W2 of the SAME org must
        // never reach it (workspace-scoped lookup; threats T1/T5).
        var seeded = await SeedWorkspaceAsync();
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
            var result = await service.ReorderAsync(
                seeded.OrganizationId, siblingWorkspaceId, seeded.Scene0, 3, _reorderTime, CancellationToken.None);
            Assert.Equal(SceneReorderResult.NotFound, result);
        }

        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene1, seeded.Scene2, seeded.Scene3);
    }

    [Fact]
    public async Task Reorder_through_a_foreign_tenant_is_not_found_and_keeps_the_orders()
    {
        // The scene lives in org A's workspace; addressing it with org B's id must never reach it
        // (organization boundary checked before workspace boundary; threat T5).
        var seeded = await SeedWorkspaceAsync();
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
            var result = await service.ReorderAsync(
                orgBId, seeded.WorkspaceId, seeded.Scene0, 3, _reorderTime, CancellationToken.None);
            Assert.Equal(SceneReorderResult.NotFound, result);
        }

        await AssertSequenceAsync(seeded, seeded.Scene0, seeded.Scene1, seeded.Scene2, seeded.Scene3);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Reorder_rejects_empty_required_ids(bool org, bool workspace, bool scene)
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderAsync(
            org ? Guid.Empty : Guid.CreateVersion7(),
            workspace ? Guid.Empty : Guid.CreateVersion7(),
            scene ? Guid.Empty : Guid.CreateVersion7(),
            0,
            _reorderTime,
            CancellationToken.None));
    }

    [Fact]
    public async Task Reorder_rejects_a_negative_target_index()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReorderAsync(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), -1, _reorderTime, CancellationToken.None));
    }

    /// <summary>
    /// Asserts the workspace's scenes are stored at contiguous, gap-free orders 0..n-1 and that the
    /// deterministic (scene_order, id) sequence equals the given scene ids in order.
    /// </summary>
    private async Task AssertSequenceAsync(SeededWorkspace seeded, params Guid[] expectedIdsInOrder)
    {
        await using var verify = CreateContext();
        var scenes = await new SceneRepository(verify)
            .ListByWorkspaceAsync(seeded.OrganizationId, seeded.WorkspaceId, CancellationToken.None);

        Assert.Equal(expectedIdsInOrder, scenes.Select(s => s.Id).ToArray());
        // Contiguous, gap-free, duplicate-free orders 0..n-1 in the same sequence.
        Assert.Equal(Enumerable.Range(0, expectedIdsInOrder.Length).ToArray(), scenes.Select(s => s.Order).ToArray());
    }

    /// <summary>Seeds one organization + workspace holding four scenes at contiguous orders 0..3.</summary>
    private async Task<SeededWorkspace> SeedWorkspaceAsync()
    {
        await using var context = CreateContext();

        var org = Organization.Create(_orgSlugA, _orgSlugA, _seedTime);
        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        var workspace = Workspace.Create(org.Id, "summer-show", "Summer Show", _seedTime);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var scene0 = Scene.Create(org.Id, workspace.Id, "Scene Zero", 0, _seedTime);
        var scene1 = Scene.Create(org.Id, workspace.Id, "Scene One", 1, _seedTime);
        var scene2 = Scene.Create(org.Id, workspace.Id, "Scene Two", 2, _seedTime);
        var scene3 = Scene.Create(org.Id, workspace.Id, "Scene Three", 3, _seedTime);
        context.Scenes.AddRange(scene0, scene1, scene2, scene3);
        await context.SaveChangesAsync();

        return new SeededWorkspace
        {
            OrganizationId = org.Id,
            WorkspaceId = workspace.Id,
            Scene0 = scene0.Id,
            Scene1 = scene1.Id,
            Scene2 = scene2.Id,
            Scene3 = scene3.Id,
        };
    }

    private sealed class SeededWorkspace
    {
        public Guid OrganizationId { get; init; }
        public Guid WorkspaceId { get; init; }
        public Guid Scene0 { get; init; }
        public Guid Scene1 { get; init; }
        public Guid Scene2 { get; init; }
        public Guid Scene3 { get; init; }
    }
}
