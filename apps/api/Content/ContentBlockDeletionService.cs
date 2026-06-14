using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Content;

/// <summary>
/// The content block deletion command of the Content module (CORE-LIFE-004, the "Resource Lifecycle and
/// Deletion" epic). A host can delete a <see cref="ContentBlock"/> from a scene; this service removes the
/// content block and CASCADES the cleanup of its dependents — its <see cref="VisibilityRule"/>s and its
/// <see cref="AssetLink"/>s — then appends an append-only audit record, all inside ONE database
/// transaction so a deletion is applied whole or not at all (the story's "visibility rules and revisions
/// are cleaned up consistently; authorized" criterion).
///
/// CASCADE, NOT BLOCK (docs/adr/0012-resource-deletion-cascades-dependents.md). The story requires the
/// dependents to be handled "consistently with CORE-LIFE-003" (the entity deletion), and ADR 0012 is the
/// governing decision: a resource deletion CASCADES its dependents inside one transaction rather than
/// blocking while any remain. The dependents of a content block are handled by their nature:
/// <list type="bullet">
///   <item><b>REVISIONS</b> are not a separate table: a content block's revision history is the inline
///   monotonic <see cref="ContentBlock.RevisionNumber"/> counter ON the <c>content_blocks</c> row
///   (csv/database_tables.csv lists only <c>content_blocks</c>, no revisions table). So removing the row
///   removes the block TOGETHER WITH its revisions — "revisions are cleaned up" needs no separate step.</item>
///   <item><b>visibility_rules</b> reference the content block POLYMORPHICALLY (<c>resource_id</c> is NOT a
///   foreign key — a single column cannot foreign-key into scenes/content_blocks/entities), so the database
///   can NOT cascade them. Leaving a rule behind would DANGLE — a stale visible rule a later resource minted
///   with the same id could inherit (a visibility leak; threats T2/T5). So this service removes every rule
///   governing the content block explicitly (<see cref="IVisibilityRuleRepository.RemoveByResourceAsync"/>),
///   the audience-wide rule AND every selected-participant rule. This is the story's headline cleanup ("its
///   visibility rules ... are cleaned up consistently").</item>
///   <item><b>asset_links</b> targeting the content block likewise reference it POLYMORPHICALLY
///   (<c>target_id</c> is NOT a foreign key), so the database cannot cascade them either. A content block is
///   a valid asset-link target (<see cref="AssetLinkTargetType.ContentBlock"/>), and a left-behind link is a
///   dangling link through which an asset could claim audience access via a target that no longer exists
///   (threat T4). So this service removes them explicitly too
///   (<see cref="IAssetLinkRepository.RemoveByTargetAsync"/>), exactly as the entity deletion does — the
///   consistent application of ADR 0012. Only the LINK rows are removed; the linked ASSETS are untouched (an
///   asset outlives a target it was linked to).</item>
/// </list>
/// A content block has NO directed relationship edges (those are entity-only), so there is no edge cleanup
/// here — the one shape of dependent the entity deletion has that the content block does not.
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant, workspace and scene and a content block
/// id, and loads the content block through the tenant-, workspace- AND scene-scoped
/// <see cref="IContentBlockRepository.FindByIdAsync(Guid, Guid, Guid, Guid, CancellationToken)"/> FIRST: a
/// content block in another scene, workspace, or tenant is never found and so never deleted, even when its
/// surrogate id is known (threats T1/T5). Every dependent removal is likewise tenant- and workspace-scoped,
/// so the cascade can never reach across a workspace or tenant boundary. The endpoint
/// (<see cref="ContentBlockEndpoints"/>) performs the authentication, tenant resolution, scene resolution and
/// role authorization before calling in; this service is the authorized command's effect.
///
/// ATOMICITY. The removals, the content block delete and the audit append run inside a single explicit
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so
/// either the whole deletion — content block, dependents and audit — commits together, or a failure rolls the
/// lot back leaving the content block and its dependents intact and no audit record written. Auditing is
/// inside the transaction on purpose: a deletion is recorded as a fact or it does not happen.
/// </summary>
internal sealed class ContentBlockDeletionService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IContentBlockRepository _contentBlocks;
    private readonly IVisibilityRuleRepository _visibilityRules;
    private readonly IAssetLinkRepository _assetLinks;
    private readonly IAuditLogRepository _audit;

    public ContentBlockDeletionService(
        TransactionalUnitOfWork unitOfWork,
        IContentBlockRepository contentBlocks,
        IVisibilityRuleRepository visibilityRules,
        IAssetLinkRepository assetLinks,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(contentBlocks);
        ArgumentNullException.ThrowIfNull(visibilityRules);
        ArgumentNullException.ThrowIfNull(assetLinks);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _contentBlocks = contentBlocks;
        _visibilityRules = visibilityRules;
        _assetLinks = assetLinks;
        _audit = audit;
    }

    /// <summary>
    /// Deletes the content block identified by <paramref name="contentBlockId"/> within the given tenant,
    /// workspace and scene, cascading its dependent visibility rules and asset links and appending an
    /// append-only <see cref="AuditAction.ContentBlockDeleted"/> record — all atomically. Returns
    /// <see cref="ContentBlockDeletionResult.Deleted"/> when the content block existed and was removed, or
    /// <see cref="ContentBlockDeletionResult.NotFound"/> when no content block with that id exists in the
    /// resolved tenant, workspace and scene (an unknown id, or one belonging to another
    /// scene/workspace/tenant — nothing is changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the content block belongs to.</param>
    /// <param name="sceneId">The scene the content block belongs to.</param>
    /// <param name="contentBlockId">The surrogate id of the content block to delete.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated host who executed the deletion — the audited actor. Supplied by the endpoint from
    /// the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, scene id, content block id or actor id is empty.
    /// </exception>
    public async Task<ContentBlockDeletionResult> DeleteAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sceneId,
        Guid contentBlockId,
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

        if (contentBlockId == Guid.Empty)
        {
            throw new ArgumentException("Content block id must not be empty.", nameof(contentBlockId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // Load the content block WITHIN the resolved tenant, workspace AND scene. The scene-scoped
        // FindByIdAsync leads with organization_id then workspace_id then scene_id then the content block
        // id, so a block in another scene, workspace or tenant is never returned even when the surrogate id
        // matches; an unknown id is simply null. Deleting a non-existent content block reveals nothing and
        // changes nothing — a safe NotFound, and no transaction is opened for it.
        var contentBlock = await _contentBlocks
            .FindByIdAsync(organizationId, workspaceId, sceneId, contentBlockId, cancellationToken)
            .ConfigureAwait(false);
        if (contentBlock is null)
        {
            return ContentBlockDeletionResult.NotFound;
        }

        // ONE unit of work (CORE-CONC-002): the dependent cascade, the content block delete and the audit
        // append commit together or roll back together, so a deletion is applied whole or not at all. Every
        // injected repository writes through the same scoped DbContext TransactionalUnitOfWork begins the
        // transaction on, so each repository SaveChanges enrols in this transaction. The transaction is opened
        // inside the EF execution strategy's ExecuteAsync, so it stays correct under the CORE-CONC-003 retrying
        // strategy (a bare user-initiated BeginTransaction would be rejected by it).
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // Polymorphic (non-FK) dependents the database can NOT cascade — remove them explicitly so no
                // dangling rule/link is left behind (threats T2/T4/T5).
                await _assetLinks
                    .RemoveByTargetAsync(organizationId, workspaceId, AssetLinkTargetType.ContentBlock, contentBlockId, transactionCancellationToken)
                    .ConfigureAwait(false);
                await _visibilityRules
                    .RemoveByResourceAsync(organizationId, workspaceId, VisibilityResourceType.ContentBlock, contentBlockId, transactionCancellationToken)
                    .ConfigureAwait(false);

                // The content block itself (its dependents are already gone). Its revision history is the inline
                // revision_number on this row, so removing the row removes the block together with its
                // revisions.
                await _contentBlocks.RemoveAsync(contentBlock, transactionCancellationToken).ConfigureAwait(false);

                // AUDIT (the consistent application of ADR 0012: "audit the deletion"): a deletion is
                // security-relevant, so append an append-only record capturing the actor (the host who deleted),
                // the deleted content block and the tenant/workspace. The audit references are recorded facts,
                // not foreign keys, so the row outlives the now-deleted content block (threats T1/T5/T7).
                var entry = AuditLogEntry.ForContentBlockDeletion(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    nameof(ContentBlock),
                    contentBlockId,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return ContentBlockDeletionResult.Deleted;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
