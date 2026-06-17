using LiveCore.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LiveCore.Api.Content;

/// <summary>
/// EF Core implementation of <see cref="IContentBlockRepository"/> (CORE-SCENE-002),
/// backed by the <c>content_blocks</c> table mapped in
/// <see cref="ContentBlockConfiguration"/>.
/// </summary>
internal sealed class ContentBlockRepository : IContentBlockRepository
{
    private readonly LiveCoreDbContext _dbContext;

    public ContentBlockRepository(LiveCoreDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    /// <inheritdoc />
    public async Task<ContentBlock?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored content block (ids are generated
        // non-empty), so the lookup fails fast instead of returning an arbitrary row.
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
            throw new ArgumentException("Content block id must not be empty.", nameof(id));
        }

        // All predicates translate to parameterized SQL equality, leading with the
        // tenant column. The lookup is exactly tenant- and workspace-scoped, so a block
        // under another organization or workspace is never returned even when the
        // surrogate id matches (threat T5/T1).
        return await _dbContext.ContentBlocks
            .FirstOrDefaultAsync(
                contentBlock => contentBlock.OrganizationId == organizationId
                    && contentBlock.WorkspaceId == workspaceId
                    && contentBlock.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentBlock?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        Guid id,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored content block, so the lookup fails fast
        // instead of returning an arbitrary row.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Content block id must not be empty.", nameof(id));
        }

        // The scene-scoped access path (the documented content_blocks(workspace_id,
        // scene_id) shape): the predicate leads with the tenant column and then matches
        // the workspace and the scene, so a block under another organization,
        // workspace or scene is never returned even when the surrogate id matches
        // (threat T5/T1).
        return await _dbContext.ContentBlocks
            .FirstOrDefaultAsync(
                contentBlock => contentBlock.OrganizationId == organizationId
                    && contentBlock.WorkspaceId == workspaceId
                    && contentBlock.SceneId == sceneId
                    && contentBlock.Id == id,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentBlock>> ListBySceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored scene's content blocks, so the lookup
        // fails fast instead of returning an arbitrary set of rows.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        // The predicate leads with the tenant column and then matches the workspace and
        // the scene, so the list is exactly tenant-, workspace- and scene-scoped:
        // another tenant's, workspace's or scene's content blocks are never returned
        // even when their ids would otherwise be addressable (threat T5/T1; the
        // organization boundary is checked before the workspace boundary). The ordering
        // is deterministic — sorted by the surrogate id, which is time-ordered
        // (UUIDv7), so the sequence is stable and repeatable.
        return await _dbContext.ContentBlocks
            .Where(contentBlock => contentBlock.OrganizationId == organizationId
                && contentBlock.WorkspaceId == workspaceId
                && contentBlock.SceneId == sceneId)
            .OrderBy(contentBlock => contentBlock.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentBlock>> ListPageBySceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored scene's content blocks, so the lookup fails fast (mirrors the
        // unbounded list).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sceneId == Guid.Empty)
        {
            throw new ArgumentException("Scene id must not be empty.", nameof(sceneId));
        }

        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least one.");
        }

        // The same tenant-, workspace- AND scene-scoped, deterministic (UUIDv7 id) read as ListBySceneAsync
        // (predicate leads with organization_id then workspace_id then scene_id, threat T5/T1), but bounded by
        // Skip/Take so an unbounded list is never materialized (threat T9).
        return await _dbContext.ContentBlocks
            .Where(contentBlock => contentBlock.OrganizationId == organizationId
                && contentBlock.WorkspaceId == workspaceId
                && contentBlock.SceneId == sceneId)
            .OrderBy(contentBlock => contentBlock.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentBlockAddResult> AddAsync(
        ContentBlock contentBlock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        _dbContext.ContentBlocks.Add(contentBlock);

        // A content block has no uniqueness constraint to violate, so there is no
        // duplicate outcome to translate here; a foreign-key violation (a non-existent
        // scene, workspace or tenant) propagates as a DbUpdateException.
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ContentBlockAddResult.Added;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ContentBlock contentBlock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        // The content block was loaded and mutated within this scope's change tracker
        // (or is attached here); only the mutable type, body, revision number and
        // update timestamp change. The organization, workspace, scene and id are
        // immutable on the aggregate, so an update can never move the row to another
        // tenant, workspace or scene (threat T5).
        _dbContext.ContentBlocks.Update(contentBlock);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(ContentBlock contentBlock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentBlock);

        // The caller resolved this exact row through the tenant-, workspace- and
        // scene-scoped FindByIdAsync, so removing the loaded content block deletes
        // precisely that one block. Its revision history is the inline revision_number on
        // this row (no separate revisions table), so the revisions go with the row; the
        // polymorphic visibility_rules and asset_links are removed by
        // ContentBlockDeletionService in the same unit of work (the database cannot cascade
        // a non-FK reference). See docs/adr/0012-resource-deletion-cascades-dependents.md.
        _dbContext.ContentBlocks.Remove(contentBlock);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
