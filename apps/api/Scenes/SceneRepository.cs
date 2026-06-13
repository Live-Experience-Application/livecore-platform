using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Scenes;

/// <summary>
/// EF Core implementation of <see cref="ISceneRepository"/> (CORE-SCENE-001), backed
/// by the <c>scenes</c> table mapped in <see cref="SceneConfiguration"/>.
/// </summary>
internal sealed class SceneRepository : ISceneRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public SceneRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<Scene?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored scene (ids are generated non-empty),
        // so the lookup fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(id));
        }

        // All three predicates translate to parameterized SQL equality, leading with
        // the tenant column. The lookup is exactly tenant- and workspace-scoped, so a
        // scene under another organization or workspace is never returned even when
        // the surrogate id matches (threat T5/T1).
        return await _dbContext.Scenes
            .FirstOrDefaultAsync(
                scene => scene.OrganizationId == organizationId
                    && scene.WorkspaceId == workspaceId
                    && scene.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Scene?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored scene (ids are generated non-empty),
        // so the lookup fails fast instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(id));
        }

        // The predicate LEADS with the tenant column, so the lookup is exactly
        // tenant-scoped: a scene under another organization is never returned even
        // when the surrogate id matches (threat T5/T1). The workspace is not part of
        // the predicate because the by-scene-id content-block route does not know it
        // up front; it is read off the returned row (Scene.WorkspaceId) so the caller
        // can authorize against workspace membership AFTER the tenant boundary has
        // been enforced (mirrors SessionRepository.FindByIdInOrganizationAsync).
        return await _dbContext.Scenes
            .FirstOrDefaultAsync(
                scene => scene.OrganizationId == organizationId
                    && scene.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Scene>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's scenes, so the lookup fails
        // fast instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        // The predicate leads with the tenant column and then matches the workspace,
        // so the list is exactly tenant- and workspace-scoped: another tenant's or
        // another workspace's scenes are never returned even when their ids would
        // otherwise be addressable (threat T5/T1; the organization boundary is checked
        // before the workspace boundary). The ordering is deterministic — sorted by
        // the scene order, then by the surrogate id so equal/duplicate orders never
        // produce a nondeterministic sequence (the ordering feature, backed by the
        // ix_scenes_workspace_id_scene_order index).
        return await _dbContext.Scenes
            .Where(scene => scene.OrganizationId == organizationId
                && scene.WorkspaceId == workspaceId)
            .OrderBy(scene => scene.Order)
            .ThenBy(scene => scene.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SceneAddResult> AddAsync(Scene scene, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);

        _dbContext.Scenes.Add(scene);

        // A scene has no uniqueness constraint to violate (the order column is
        // deliberately non-unique), so there is no duplicate outcome to translate
        // here; a foreign-key violation (a non-existent workspace or tenant)
        // propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SceneAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Scene scene, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // The scene was loaded and mutated within this scope's change tracker (or is
        // attached here); only the mutable title, order and update timestamp change.
        // The organization, workspace and id are immutable on the aggregate, so an
        // update can never move the row to another tenant or workspace (threat T5).
        _dbContext.Scenes.Update(scene);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(Scene scene, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);

        // The caller resolved this exact row through the tenant- and workspace-scoped
        // FindByIdAsync, so removing the loaded scene deletes precisely that one scene.
        // The database FK cascades this delete to the scene's child content blocks
        // (content_blocks.scene_id ON DELETE CASCADE); the SceneDeletionService removes
        // those content blocks AND every polymorphic visibility_rule / asset_link the
        // database cannot cascade in the same unit of work BEFORE this call, so the
        // cascade is deterministic and nothing dangles. See
        // docs/adr/0012-resource-deletion-cascades-dependents.md.
        _dbContext.Scenes.Remove(scene);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
