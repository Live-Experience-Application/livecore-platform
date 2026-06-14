using LiveCore.Api.Assets;
using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Entities;

/// <summary>
/// The entity deletion command of the Entities module (CORE-LIFE-003, the "Resource Lifecycle and
/// Deletion" epic). A host can delete an <see cref="Entity"/>; this service removes the entity and
/// CASCADES the cleanup of its dependents — its directed <see cref="EntityRelationship"/> edges, its
/// <see cref="VisibilityRule"/>s and its <see cref="AssetLink"/>s — then appends an append-only audit
/// record, all inside ONE database transaction so a deletion is applied whole or not at all (the story's
/// "handled consistently … authorized and audited" criterion).
///
/// CASCADE, NOT BLOCK (docs/adr/0012-resource-deletion-cascades-dependents.md). The story requires a
/// deliberate, documented choice between cascading the dependents and blocking the deletion while any
/// remain. Core CASCADES, for two reasons that differ per dependent:
/// <list type="bullet">
///   <item><b>EntityRelationship edges</b> hold REAL foreign keys to the entity
///   (<c>source_entity_id</c>/<c>target_entity_id</c>, both <c>ON DELETE CASCADE</c>), so the database
///   would itself cascade them. This service removes them EXPLICITLY first anyway
///   (<see cref="IEntityRelationshipRepository.RemoveByEntityAsync"/>) so the cascade is deterministic,
///   observable and identical across providers, and so there is never an instant where a dangling edge
///   exists — the database cascade then has nothing left to do (defence in depth).</item>
///   <item><b>visibility_rules</b> and <b>asset_links</b> reference the entity POLYMORPHICALLY
///   (<c>resource_id</c> / <c>target_id</c> are NOT foreign keys — a single column cannot foreign-key
///   into several resource tables), so the database can NOT cascade them. Removing them is the whole
///   point of doing this in the application: a left-behind rule or link would DANGLE — a stale visible
///   rule a later resource minted with the same id could inherit (a visibility leak; threats T2/T5), or
///   a link through which an asset could claim audience access via a target that no longer exists
///   (threat T4). So this service removes them explicitly
///   (<see cref="IVisibilityRuleRepository.RemoveByResourceAsync"/>,
///   <see cref="IAssetLinkRepository.RemoveByTargetAsync"/>).</item>
/// </list>
/// Blocking was rejected: a generic Core entity is host-prepared content with no published external
/// contract that another tenant relies on, so the host who deletes it intends the dependents to go too;
/// blocking would strand an undeletable entity behind rules/links the same host must hunt down first,
/// with no safety benefit (the dependents are all in the host's own workspace).
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace and an entity id, and
/// loads the entity through the tenant- AND workspace-scoped <see cref="IEntityRepository.FindByIdAsync"/>
/// FIRST: an entity in another workspace, or in a workspace owned by another tenant, is never found and
/// so never deleted, even when its surrogate id is known (threats T1/T5). Every dependent removal is
/// likewise tenant- and workspace-scoped, so the cascade can never reach across a workspace or tenant
/// boundary. The endpoint above (<see cref="EntityEndpoints"/>) performs the authentication, tenant
/// resolution and role authorization before calling in; this service is the authorized command's effect.
///
/// ATOMICITY. The removals, the entity delete and the audit append run inside a single explicit
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so
/// either the whole deletion — entity, dependents and audit — commits together, or a failure rolls the
/// lot back leaving the entity and its dependents intact and no audit record written. Auditing is inside
/// the transaction on purpose: a deletion is recorded as a fact or it does not happen.
/// </summary>
internal sealed class EntityDeletionService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IEntityRepository _entities;
    private readonly IEntityRelationshipRepository _relationships;
    private readonly IVisibilityRuleRepository _visibilityRules;
    private readonly IAssetLinkRepository _assetLinks;
    private readonly IAuditLogRepository _audit;

    public EntityDeletionService(
        TransactionalUnitOfWork unitOfWork,
        IEntityRepository entities,
        IEntityRelationshipRepository relationships,
        IVisibilityRuleRepository visibilityRules,
        IAssetLinkRepository assetLinks,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(visibilityRules);
        ArgumentNullException.ThrowIfNull(assetLinks);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _entities = entities;
        _relationships = relationships;
        _visibilityRules = visibilityRules;
        _assetLinks = assetLinks;
        _audit = audit;
    }

    /// <summary>
    /// Deletes the entity identified by <paramref name="entityId"/> within the given tenant and workspace,
    /// cascading its dependent edges, visibility rules and asset links and appending an append-only
    /// <see cref="AuditAction.EntityDeleted"/> record — all atomically. Returns
    /// <see cref="EntityDeletionResult.Deleted"/> when the entity existed and was removed, or
    /// <see cref="EntityDeletionResult.NotFound"/> when no entity with that id exists in the resolved
    /// tenant and workspace (an unknown id, or one belonging to another workspace/tenant — nothing is
    /// changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the entity belongs to.</param>
    /// <param name="entityId">The surrogate id of the entity to delete.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated host who executed the deletion — the audited actor. Supplied by the endpoint from
    /// the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, entity id or actor id is empty.
    /// </exception>
    public async Task<EntityDeletionResult> DeleteAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityId,
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

        if (entityId == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(entityId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // Load the entity WITHIN the resolved tenant AND workspace. FindByIdAsync leads with
        // organization_id then workspace_id then the entity id, so an entity in another workspace or
        // tenant is never returned even when the surrogate id matches; an unknown id is simply null.
        // Deleting a non-existent entity reveals nothing and changes nothing — a safe NotFound, and no
        // transaction is opened for it.
        var entity = await _entities
            .FindByIdAsync(organizationId, workspaceId, entityId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return EntityDeletionResult.NotFound;
        }

        // ONE unit of work (CORE-CONC-002): the dependent cascade, the entity delete and the audit append
        // commit together or roll back together, so a deletion is applied whole or not at all. Every injected
        // repository writes through the same scoped DbContext TransactionalUnitOfWork begins the transaction
        // on, so each repository SaveChanges enrols in this transaction. The transaction is opened inside the
        // EF execution strategy's ExecuteAsync, so it stays correct under the CORE-CONC-003 retrying strategy
        // (a bare user-initiated BeginTransaction would be rejected by a retrying strategy).
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // Polymorphic (non-FK) dependents the database can NOT cascade — remove them explicitly so no
                // dangling rule/link is left behind (threats T2/T4/T5).
                await _assetLinks
                    .RemoveByTargetAsync(organizationId, workspaceId, AssetLinkTargetType.Entity, entityId, transactionCancellationToken)
                    .ConfigureAwait(false);
                await _visibilityRules
                    .RemoveByResourceAsync(organizationId, workspaceId, VisibilityResourceType.Entity, entityId, transactionCancellationToken)
                    .ConfigureAwait(false);

                // FK-backed dependents (the database would cascade these); remove them explicitly first so the
                // cascade is deterministic and provider-independent and there is never a dangling edge.
                await _relationships
                    .RemoveByEntityAsync(organizationId, workspaceId, entityId, transactionCancellationToken)
                    .ConfigureAwait(false);

                // The entity itself last (its dependents are already gone).
                await _entities.RemoveAsync(entity, transactionCancellationToken).ConfigureAwait(false);

                // AUDIT (the story's "authorized and audited" criterion): a deletion is security-relevant, so
                // append an append-only record capturing the actor (the host who deleted), the deleted entity
                // and the tenant/workspace. The audit references are recorded facts, not foreign keys, so the
                // row outlives the now-deleted entity (threats T1/T5/T7).
                var entry = AuditLogEntry.ForEntityDeletion(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    nameof(Entity),
                    entityId,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return EntityDeletionResult.Deleted;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
