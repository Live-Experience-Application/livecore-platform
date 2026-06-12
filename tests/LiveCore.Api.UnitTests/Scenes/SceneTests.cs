using LiveCore.Api.Scenes;

namespace LiveCore.Api.UnitTests.Scenes;

/// <summary>
/// Unit tests for the <see cref="Scene"/> invariants, its tenant/workspace boundary
/// and its ordering (CORE-SCENE-001): a scene is the generic "ordered segment" of a
/// workspace (docs/03_DOMAIN_LANGUAGE.md); it is workspace-scoped and tenant-scoped
/// and belongs only to the workspace and organization it names; its
/// <see cref="Scene.Order"/> is an explicit non-negative position that
/// <see cref="Scene.Reorder"/> changes without moving the scene; and ToString stays
/// log-safe (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). No vertical-specific
/// terms appear anywhere — generic Scene vocabulary only (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class SceneTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 0, 0, TimeSpan.Zero);

    private static Scene CreateScene(int order = 0)
        => Scene.Create(_organizationId, _workspaceId, "Opening Segment", order, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_sets_the_metadata_and_order()
    {
        var scene = Scene.Create(_organizationId, _workspaceId, "Opening Segment", 3, _createdAt);

        Assert.NotEqual(Guid.Empty, scene.Id);
        Assert.Equal(_organizationId, scene.OrganizationId);
        Assert.Equal(_workspaceId, scene.WorkspaceId);
        Assert.Equal("Opening Segment", scene.Title);
        Assert.Equal(3, scene.Order);
        Assert.Equal(_createdAt, scene.CreatedAt);
        Assert.Equal(_createdAt, scene.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreateScene();
        var second = CreateScene();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_title()
    {
        var scene = Scene.Create(_organizationId, _workspaceId, "  Trimmed Title  ", 0, _createdAt);

        Assert.Equal("Trimmed Title", scene.Title);
    }

    [Fact]
    public void Create_allows_a_zero_order()
    {
        // Zero is the first position and a valid order (the lower bound).
        var scene = CreateScene(order: 0);

        Assert.Equal(0, scene.Order);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var scene = Scene.Create(_organizationId, _workspaceId, "Title", 0, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, scene.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), scene.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => Scene.Create(Guid.Empty, _workspaceId, "Title", 0, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => Scene.Create(_organizationId, Guid.Empty, "Title", 0, _createdAt));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_title(string? title)
        => Assert.Throws<ArgumentException>(
            () => Scene.Create(_organizationId, _workspaceId, title!, 0, _createdAt));

    [Fact]
    public void Create_rejects_a_title_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => Scene.Create(_organizationId, _workspaceId, "bad\ttitle", 0, _createdAt));

    [Fact]
    public void Create_rejects_a_title_over_the_length_bound()
    {
        var tooLong = new string('a', Scene.MaxTitleLength + 1);

        Assert.Throws<ArgumentException>(
            () => Scene.Create(_organizationId, _workspaceId, tooLong, 0, _createdAt));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void Create_rejects_a_negative_order(int order)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Scene.Create(_organizationId, _workspaceId, "Title", order, _createdAt));

    [Fact]
    public void IsValidOrder_accepts_non_negative_and_rejects_negative()
    {
        Assert.True(Scene.IsValidOrder(0));
        Assert.True(Scene.IsValidOrder(42));
        Assert.True(Scene.IsValidOrder(int.MaxValue));
        Assert.False(Scene.IsValidOrder(-1));
        Assert.False(Scene.IsValidOrder(int.MinValue));
    }

    [Fact]
    public void Constructing_with_an_updated_before_created_is_rejected()
    {
        // The aggregate enforces updatedAt >= createdAt: a scene cannot be updated
        // before it was created. The Create factory always passes createdAt ==
        // updatedAt, so the invariant is reached through the private materialization
        // constructor (the path a corrupt/hand-built row would take). The guard throws
        // an ArgumentException, surfaced through reflection as the inner exception.
        var constructor = typeof(Scene).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(Guid), typeof(Guid), typeof(Guid), typeof(string),
                typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset),
            },
            modifiers: null);
        Assert.NotNull(constructor);

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            constructor.Invoke(new object[]
            {
                Guid.CreateVersion7(),
                _organizationId,
                _workspaceId,
                "Title",
                0,
                // createdAt is _updatedAt (later); updatedAt is _createdAt (earlier).
                _updatedAt,
                _createdAt,
            }));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    // --- Tenant / workspace boundary -------------------------------------------

    [Fact]
    public void Scene_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a scene belongs only to the organization
        // it names. The organization boundary is checked before the workspace
        // boundary.
        var scene = CreateScene();

        Assert.True(scene.BelongsToOrganization(_organizationId));
        Assert.False(scene.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(scene.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Scene_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): a scene in workspace W1 is not in
        // workspace W2.
        var scene = CreateScene();

        Assert.True(scene.BelongsToWorkspace(_workspaceId));
        Assert.False(scene.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(scene.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A scene is the one identified by its id only WITHIN its tenant and
        // workspace: the surrogate id is never honored across a foreign tenant or
        // workspace (threat T1/T5).
        var scene = CreateScene();

        Assert.True(scene.Identifies(_organizationId, _workspaceId, scene.Id));
        Assert.False(scene.Identifies(_foreignOrganizationId, _workspaceId, scene.Id));
        Assert.False(scene.Identifies(_organizationId, _foreignWorkspaceId, scene.Id));
        Assert.False(scene.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(scene.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Rename ----------------------------------------------------------------

    [Fact]
    public void Rename_changes_only_the_title_and_timestamp()
    {
        var scene = CreateScene(order: 5);

        scene.Rename("New Title", _updatedAt);

        Assert.Equal("New Title", scene.Title);
        Assert.Equal(_updatedAt, scene.UpdatedAt);
        Assert.Equal(_createdAt, scene.CreatedAt);
        // The tenant boundary, the workspace, the id and the order are immutable
        // across a rename (threat T5): renaming never moves the scene.
        Assert.Equal(_organizationId, scene.OrganizationId);
        Assert.Equal(_workspaceId, scene.WorkspaceId);
        Assert.Equal(5, scene.Order);
    }

    [Fact]
    public void Rename_trims_the_title()
    {
        var scene = CreateScene();

        scene.Rename("  Spaced  ", _updatedAt);

        Assert.Equal("Spaced", scene.Title);
    }

    [Fact]
    public void Rename_rejects_a_blank_title_and_leaves_the_scene_untouched()
    {
        var scene = CreateScene();

        Assert.Throws<ArgumentException>(() => scene.Rename("   ", _updatedAt));

        Assert.Equal("Opening Segment", scene.Title);
        Assert.Equal(_createdAt, scene.UpdatedAt);
    }

    // --- Reorder (the ordering feature) ----------------------------------------

    [Fact]
    public void Reorder_changes_only_the_order_and_timestamp()
    {
        var scene = CreateScene(order: 2);
        var sceneId = scene.Id;

        scene.Reorder(7, _updatedAt);

        Assert.Equal(7, scene.Order);
        Assert.Equal(_updatedAt, scene.UpdatedAt);
        Assert.Equal(_createdAt, scene.CreatedAt);
        // The tenant boundary, the workspace, the id and the title are immutable
        // across a reorder (threat T5): moving a scene's position never moves it to
        // another tenant/workspace and never changes its display label.
        Assert.Equal(sceneId, scene.Id);
        Assert.Equal(_organizationId, scene.OrganizationId);
        Assert.Equal(_workspaceId, scene.WorkspaceId);
        Assert.Equal("Opening Segment", scene.Title);
    }

    [Fact]
    public void Reorder_allows_a_zero_order()
    {
        var scene = CreateScene(order: 4);

        scene.Reorder(0, _updatedAt);

        Assert.Equal(0, scene.Order);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Reorder_rejects_a_negative_order_and_leaves_the_scene_untouched(int order)
    {
        var scene = CreateScene(order: 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => scene.Reorder(order, _updatedAt));

        // The rejected reorder did not change the position or re-stamp the update
        // timestamp.
        Assert.Equal(3, scene.Order);
        Assert.Equal(_createdAt, scene.UpdatedAt);
    }

    [Fact]
    public void Reorder_normalizes_the_timestamp_to_utc()
    {
        var localUpdatedAt = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.FromHours(2));
        var scene = CreateScene(order: 1);

        scene.Reorder(9, localUpdatedAt);

        Assert.Equal(TimeSpan.Zero, scene.UpdatedAt.Offset);
        Assert.Equal(localUpdatedAt.ToUniversalTime(), scene.UpdatedAt);
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_and_order_but_no_title()
    {
        // Log-safety (threat T7): structured logs carry identifiers and the ordering
        // position, never the title (free-form metadata content).
        var scene = CreateScene(order: 6);

        var text = scene.ToString();

        Assert.Contains(scene.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("order=6", text, StringComparison.Ordinal);
        // The title is never written to logs.
        Assert.DoesNotContain("Opening Segment", text, StringComparison.Ordinal);
    }
}
