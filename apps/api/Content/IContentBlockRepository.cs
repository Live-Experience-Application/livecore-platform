// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Content;

/// <summary>
/// Persistence contract for the content block aggregate (CORE-SCENE-002). The Content
/// module owns the <c>content_blocks</c> table; other modules access content blocks
/// only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Content module owns "content blocks" and "content
/// block revisions").
///
/// Every lookup is explicitly scoped by the tenant and workspace boundaries (and, for
/// the scene-scoped reads, the scene): the caller passes the organization id, the
/// workspace id (and the scene id), and a content block is only ever returned when it
/// belongs to exactly that scope. The organization boundary is checked before the
/// workspace boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a
/// content block is never returned through a foreign organization's id even when the
/// workspace, scene and block ids are correct, never through a foreign workspace's id,
/// and never through a foreign scene's id. There is deliberately no lookup of a
/// content block by id alone, no lookup that crosses tenants and NO list-everything
/// method, so one scene's content block can never be read through another scene's,
/// workspace's or tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat
/// T1 broken object-level authorization). Resolving the "current" organization,
/// workspace or scene from a request is not done here; that is the tenant context
/// resolver (CORE-ID-005) and later endpoint stories. This is the aggregate +
/// revisions + persistence story; the content HTTP endpoints (CORE-SCENE-003), the
/// host/participant DTO projection (CORE-SCENE-004), content validation/size limits
/// (CORE-SCENE-005) and the server-side visibility decision (the Visibility epic) are
/// LATER stories and are deliberately not built here. This contract takes explicit
/// ids.
/// </summary>
public interface IContentBlockRepository
{
    /// <summary>
    /// Finds the content block with exactly the given id WITHIN the given organization
    /// and workspace, or <see langword="null"/> when no such block exists there. The
    /// organization and workspace both scope the lookup, so a block that exists under
    /// another organization's or workspace's id is never returned, even when the
    /// surrogate id matches (threat T5/T1). This overload does not take a scene id: it
    /// resolves a block already known to belong to the workspace (the scene-scoped
    /// overload is used when the scene is part of the access path).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or content block id is empty. An empty id can
    /// never address a stored block, so the lookup is rejected instead of silently
    /// returning nothing.
    /// </exception>
    Task<ContentBlock?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the content block with exactly the given id WITHIN the given organization,
    /// workspace AND scene, or <see langword="null"/> when no such block exists there.
    /// All four scope the lookup, so a block under another organization's, workspace's
    /// or scene's id is never returned, even when the surrogate id matches (threat
    /// T5/T1). This is the scene-scoped access path (the documented
    /// <c>content_blocks(workspace_id, scene_id)</c> shape).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, scene id or content block id is empty. An
    /// empty id can never address a stored block, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<ContentBlock?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every content block of the given scene (of the given workspace, owned by
    /// the given organization) in a deterministic order — sorted by the surrogate id,
    /// which is time-ordered (UUIDv7), so the sequence is stable and repeatable and
    /// ties never produce a nondeterministic order.
    ///
    /// The list is tenant-, workspace- AND scene-scoped: the predicate leads with
    /// <c>organization_id</c>, then matches <c>workspace_id</c> and <c>scene_id</c>, so
    /// a foreign tenant's, workspace's or scene's content blocks are NEVER returned
    /// even when their ids would otherwise be addressable (threat T5/T1;
    /// docs/06_AUTHORIZATION_MATRIX.md: the organization boundary is checked before the
    /// workspace boundary). An empty list is returned for a scene that has no content
    /// blocks; the lookup never crosses a boundary to borrow another scene's blocks.
    /// This is NOT a list-everything method: there is no way to read content blocks
    /// outside an explicit (organization, workspace, scene) scope.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or scene id is empty. An empty id can never
    /// address a stored scene's content blocks, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<IReadOnlyList<ContentBlock>> ListBySceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the given scene's content blocks (of the given workspace, owned by the given
    /// organization) in the same deterministic (UUIDv7 id) order as <see cref="ListBySceneAsync"/>, limited to
    /// <paramref name="take"/> rows starting at <paramref name="skip"/>, backing the bounded content-block list
    /// route (<c>GET /api/v1/scenes/{sceneId}/content-blocks</c>, CORE-DX-003). The page is exactly tenant-,
    /// workspace- AND scene-scoped (the predicate leads with <c>organization_id</c> then matches
    /// <c>workspace_id</c> and <c>scene_id</c>), so a foreign tenant's, workspace's or scene's blocks are NEVER
    /// returned (threat T5/T1). The caller over-fetches one extra row (<c>take = limit + 1</c>) to decide
    /// <c>hasMore</c> without a second COUNT. Bounding the read means a single list response can never
    /// materialize the whole table (threat T9).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id, workspace id or scene id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<ContentBlock>> ListPageBySceneAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new content block. A content block has no natural key (it is
    /// identified only by its surrogate id), so there is no uniqueness outcome to
    /// report; the result is always <see cref="ContentBlockAddResult.Added"/> on
    /// success. Foreign-key violations (a non-existent scene, workspace or tenant)
    /// surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<ContentBlockAddResult> AddAsync(ContentBlock contentBlock, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a content block previously loaded through this repository —
    /// in particular a new revision (a replaced body, possibly a re-typed block and an
    /// incremented revision number). The organization, workspace, scene and id of a
    /// content block are immutable (<see cref="ContentBlock"/>), so an update only ever
    /// changes the type, the body, the revision number and the update timestamp; it can
    /// never move the block to another tenant, workspace or scene (threat T5). The
    /// caller is responsible for having loaded the block through a scoped lookup.
    /// </summary>
    Task UpdateAsync(ContentBlock contentBlock, CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES the given content block row (CORE-LIFE-004, the "Resource Lifecycle
    /// and Deletion" epic). The caller
    /// (<see cref="ContentBlockDeletionService"/>) is responsible for having loaded the
    /// content block through the tenant-, workspace- AND scene-scoped
    /// <see cref="FindByIdAsync(Guid, Guid, Guid, Guid, CancellationToken)"/> first, so a
    /// foreign tenant's, workspace's or scene's content block is never reachable to remove
    /// (threat T5/T1); this method removes exactly the row it is handed. The content
    /// block's REVISIONS live inline on this row (the monotonic
    /// <see cref="ContentBlock.RevisionNumber"/> counter — csv/database_tables.csv lists no
    /// separate content-block revisions table), so removing the row removes the block
    /// together with its revision history; no separate revision cleanup is needed. The
    /// dependent <c>visibility_rules</c> and <c>asset_links</c> reference the content block
    /// POLYMORPHICALLY (no foreign key) so the database cannot cascade them — the
    /// application <see cref="ContentBlockDeletionService"/> removes those in the SAME unit
    /// of work BEFORE this call, consistently with the entity deletion
    /// (docs/adr/0012-resource-deletion-cascades-dependents.md).
    /// </summary>
    /// <exception cref="ArgumentNullException">The content block is null.</exception>
    Task RemoveAsync(ContentBlock contentBlock, CancellationToken cancellationToken);
}
