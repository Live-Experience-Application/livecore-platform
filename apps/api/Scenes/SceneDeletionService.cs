using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Content;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Scenes;

/// <summary>
/// The scene deletion command of the Scenes module (CORE-LIFE-005, the "Resource Lifecycle and Deletion"
/// epic). A host can delete a <see cref="Scene"/>; this service removes the scene and CASCADES the cleanup
/// of its dependents — its child <see cref="ContentBlock"/>s (and their dependent visibility rules and
/// asset links) and the scene's OWN visibility rules — then RE-PACKS the remaining scenes' ordering so
/// there is no gap, and appends an append-only audit record, all inside ONE database transaction so a
/// deletion is applied whole or not at all (the story's "remaining scenes re-pack their ordering without
/// gaps; child content blocks are removed; authorized" criteria).
///
/// CASCADE, NOT BLOCK (docs/adr/0012-resource-deletion-cascades-dependents.md). The story requires the
/// child content blocks to be removed "consistently with CORE-LIFE-004" (the content-block deletion), and
/// ADR 0012 is the governing decision: a resource deletion CASCADES its dependents inside one transaction
/// rather than blocking while any remain. A scene's dependents come in three shapes and are handled by
/// their nature:
/// <list type="bullet">
///   <item><b>Child content_blocks</b> reference the scene through a REAL foreign key
///   (<c>scene_id</c>, <c>ON DELETE CASCADE</c>), so the database would itself cascade them when the scene
///   row is removed. This service removes them EXPLICITLY first anyway (so the cascade is deterministic,
///   observable and identical across providers, and there is never a dangling block) — AND, for each child
///   block, removes the block's OWN polymorphic visibility_rules and asset_links, which the database can NOT
///   cascade (see below). A content block's revision history is the inline <see cref="ContentBlock.RevisionNumber"/>
///   counter on its row, so removing the row removes its revisions with it. This reuses exactly the
///   per-content-block cleanup CORE-LIFE-004 performs, but as a consequence of the ONE scene deletion (so it
///   is audited once, as the scene deletion, not once per block).</item>
///   <item><b>The scene's own visibility_rules</b> reference the scene POLYMORPHICALLY
///   (<c>resource_id</c> is NOT a foreign key — a single column cannot foreign-key into
///   scenes/content_blocks/entities), so the database can NOT cascade them. A <see cref="Scene"/> is a valid
///   visibility resource (<see cref="VisibilityResourceType.Scene"/>), and a left-behind visible rule a later
///   resource minted with the same id could inherit is a visibility leak (threats T2/T5). So this service
///   removes every rule governing the scene explicitly (<see cref="IVisibilityRuleRepository.RemoveByResourceAsync"/>),
///   the audience-wide rule AND every selected-participant rule.</item>
///   <item><b>asset_links</b> — a <see cref="Scene"/> is deliberately NOT an asset-link target (only
///   <see cref="AssetLinkTargetType.ContentBlock"/> and <see cref="AssetLinkTargetType.Entity"/> are), so the
///   scene itself has no asset links to remove; only its child content blocks' asset links (handled above)
///   are cleaned up.</item>
/// </list>
///
/// RE-PACK THE ORDERING (the story's headline requirement). After the scene is gone, the remaining scenes
/// of the workspace are re-packed to a contiguous, gap-free ordering. The re-pack REUSES the SCENE-001
/// ordering logic: it lists the survivors in the deterministic (scene_order, id) order
/// (<see cref="ISceneRepository.ListByWorkspaceAsync"/>) and moves each, in that order, to its index
/// position through <see cref="Scene.Reorder"/> — the same aggregate ordering method the create endpoint's
/// append-to-end uses — persisting only the scenes whose position actually changed. The relative order of
/// the survivors is preserved; only the gap the deleted scene left is closed.
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace and a scene id, and loads
/// the scene through the tenant- AND workspace-scoped <see cref="ISceneRepository.FindByIdAsync(Guid, Guid, Guid, CancellationToken)"/>
/// FIRST: a scene in another workspace, or in a workspace owned by another tenant, is never found and so
/// never deleted, even when its surrogate id is known (threats T1/T5). Every dependent removal and the
/// re-pack are likewise tenant- and workspace-scoped, so the cascade can never reach across a workspace or
/// tenant boundary. The endpoint (<see cref="SceneEndpoints"/>) performs the authentication, tenant
/// resolution and role authorization before calling in; this service is the authorized command's effect.
///
/// ATOMICITY. The removals, the scene delete, the order re-pack and the audit append run inside a single
/// explicit <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so
/// either the whole deletion — scene, child content blocks, dependents, re-packed ordering and audit —
/// commits together, or a failure rolls the lot back leaving everything intact and no audit record written.
/// Auditing is inside the transaction on purpose: a deletion is recorded as a fact or it does not happen.
/// </summary>
internal sealed class SceneDeletionService
{
    private readonly LiveCoreDbContext _dbContext;
    private readonly ISceneRepository _scenes;
    private readonly IContentBlockRepository _contentBlocks;
    private readonly IVisibilityRuleRepository _visibilityRules;
    private readonly IAssetLinkRepository _assetLinks;
    private readonly IAuditLogRepository _audit;

    public SceneDeletionService(
        LiveCoreDbContext dbContext,
        ISceneRepository scenes,
        IContentBlockRepository contentBlocks,
        IVisibilityRuleRepository visibilityRules,
        IAssetLinkRepository assetLinks,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(contentBlocks);
        ArgumentNullException.ThrowIfNull(visibilityRules);
        ArgumentNullException.ThrowIfNull(assetLinks);
        ArgumentNullException.ThrowIfNull(audit);
        _dbContext = dbContext;
        _scenes = scenes;
        _contentBlocks = contentBlocks;
        _visibilityRules = visibilityRules;
        _assetLinks = assetLinks;
        _audit = audit;
    }

    /// <summary>
    /// Deletes the scene identified by <paramref name="sceneId"/> within the given tenant and workspace,
    /// cascading its child content blocks and the dependent visibility rules / asset links, re-packing the
    /// remaining scenes' ordering and appending an append-only <see cref="AuditAction.SceneDeleted"/> record
    /// — all atomically. Returns <see cref="SceneDeletionResult.Deleted"/> when the scene existed and was
    /// removed, or <see cref="SceneDeletionResult.NotFound"/> when no scene with that id exists in the
    /// resolved tenant and workspace (an unknown id, or one belonging to another workspace/tenant — nothing
    /// is changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the scene belongs to.</param>
    /// <param name="sceneId">The surrogate id of the scene to delete.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated host who executed the deletion — the audited actor. Supplied by the endpoint from
    /// the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the audit record's time and the re-pack update timestamp).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, scene id or actor id is empty.
    /// </exception>
    public async Task<SceneDeletionResult> DeleteAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        Guid actorUserProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // Load the scene WITHIN the resolved tenant AND workspace. FindByIdAsync leads with organization_id
        // then workspace_id then the scene id, so a scene in another workspace or tenant is never returned
        // even when the surrogate id matches; an unknown id is simply null. Deleting a non-existent scene
        // reveals nothing and changes nothing — a safe NotFound, and no transaction is opened for it.
        var scene = await _scenes
            .FindByIdAsync(organizationId, workspaceId, sceneId, cancellationToken)
            .ConfigureAwait(false);
        if (scene is null)
        {
            return SceneDeletionResult.NotFound;
        }

        // ONE unit of work: the dependent cascade, the scene delete, the order re-pack and the audit append
        // commit together or roll back together, so a deletion is applied whole or not at all. Every injected
        // repository writes through this same scoped DbContext, so each repository SaveChanges enrols in this
        // transaction.
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Cascade the scene's CHILD CONTENT BLOCKS (the story's "child content blocks are removed"). For each
        // block, remove its OWN polymorphic dependents the database can NOT cascade (asset_links targeting it,
        // then its visibility_rules) before removing the block row — exactly the per-block cleanup
        // CORE-LIFE-004 performs, but here as a consequence of the one scene deletion (audited once, below).
        // A block's revision history is inline on its row, so it goes with the row.
        var contentBlocks = await _contentBlocks
            .ListBySceneAsync(organizationId, workspaceId, sceneId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var contentBlock in contentBlocks)
        {
            await _assetLinks
                .RemoveByTargetAsync(organizationId, workspaceId, AssetLinkTargetType.ContentBlock, contentBlock.Id, cancellationToken)
                .ConfigureAwait(false);
            await _visibilityRules
                .RemoveByResourceAsync(organizationId, workspaceId, VisibilityResourceType.ContentBlock, contentBlock.Id, cancellationToken)
                .ConfigureAwait(false);
            await _contentBlocks.RemoveAsync(contentBlock, cancellationToken).ConfigureAwait(false);
        }

        // Cascade the SCENE'S OWN polymorphic visibility rules the database can NOT cascade — both the
        // audience-wide rule and every selected-participant rule governing the scene (threats T2/T5). A scene
        // is a visibility resource but never an asset-link target, so there are no scene asset_links to remove.
        await _visibilityRules
            .RemoveByResourceAsync(organizationId, workspaceId, VisibilityResourceType.Scene, sceneId, cancellationToken)
            .ConfigureAwait(false);

        // The scene itself (its child content blocks and dependents are already gone). The database FK would
        // also cascade the (already-removed) content blocks, so the explicit removal above leaves the cascade
        // with nothing to do — defence in depth.
        await _scenes.RemoveAsync(scene, cancellationToken).ConfigureAwait(false);

        // RE-PACK the remaining scenes' ordering so the gap the deleted scene left is closed (the story's
        // "remaining scenes re-pack their ordering without gaps"). The survivors are listed in the
        // deterministic (scene_order, id) order — the SCENE-001 ordering — and each is moved, in that order,
        // to its contiguous index position through Scene.Reorder (the SCENE-001 aggregate ordering method).
        // Only the scenes whose position actually changed are persisted; the relative order is preserved.
        var remaining = await _scenes
            .ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < remaining.Count; index++)
        {
            var sibling = remaining[index];
            if (sibling.Order != index)
            {
                sibling.Reorder(index, now);
                await _scenes.UpdateAsync(sibling, cancellationToken).ConfigureAwait(false);
            }
        }

        // AUDIT (the story's "authorized" deletion, the consistent application of ADR 0012: "audit the
        // deletion"): a deletion is security-relevant, so append an append-only record capturing the actor
        // (the host who deleted), the deleted scene and the tenant/workspace. The audit references are
        // recorded facts, not foreign keys, so the row outlives the now-deleted scene (threats T1/T5/T7). The
        // cascaded content blocks/rules/links and the order re-pack are consequences of this one action and
        // are not separately audited.
        var entry = AuditLogEntry.ForSceneDeletion(
            organizationId,
            workspaceId,
            actorUserProfileId,
            nameof(Scene),
            sceneId,
            now);
        await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return SceneDeletionResult.Deleted;
    }
}
