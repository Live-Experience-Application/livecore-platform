using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Entities;

/// <summary>
/// The entity create command of the Entities module (CORE-ENT-006, the "Vertical Authoring and Read API
/// Completeness" epic). An authoring role can author a new generic <see cref="Entity"/> in a workspace; this
/// service resolves the entity's <see cref="EntityType"/> within the caller's own (organization, workspace),
/// creates the entity with a SERVER-MINTED surrogate id, persists it and appends an append-only
/// <see cref="AuditAction.EntityCreated"/> audit record — the create + audit committing together in ONE
/// database transaction so a creation is recorded as a fact or it does not happen (the story's "create … is
/// tenant/workspace-scoped and audited" criterion).
///
/// SAME-WORKSPACE-TYPE coupling (the documented carry-over on <see cref="Entity"/> and
/// <see cref="IEntityRepository"/>). An entity is an instance OF an entity type, and the
/// <c>entities.entity_type_id</c> foreign key guarantees the type EXISTS but NOT that it lives in the
/// entity's own workspace — exactly like <c>ContentBlock.SceneId</c>. The create application flow is
/// responsible for enforcing that coupling, so this service resolves the type through the tenant- AND
/// workspace-scoped <see cref="IEntityTypeRepository.FindByIdAsync"/> FIRST: a type in another workspace or
/// tenant, or an unknown type, resolves to <see langword="null"/> and yields
/// <see cref="EntityCreationResult.UnknownEntityType"/> — no entity is created and no transaction is opened
/// (mirroring how <see cref="EntityDeletionService"/> returns NotFound without opening a transaction). The
/// surrogate id alone never authorizes anything; every lookup is scoped by (organization, workspace), so the
/// type reference can never reach across a workspace or tenant boundary (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace (the endpoint performed the
/// authentication, tenant resolution and role authorization before calling in; this service is the authorized
/// command's effect). The created entity is bound to exactly that (organization, workspace) — the
/// <see cref="Entity"/> aggregate fixes them immutably at construction — so it can never be authored into
/// another tenant or workspace (threat T5).
///
/// CONTENT BOUNDARY (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). The entity's
/// <see cref="Entity.Name"/> and <see cref="Entity.AttributeValues"/> are template-/host-supplied DATA,
/// validated only for shape (a valid name, well-formed bounded JSON) by the aggregate — never inspected for
/// vocabulary, never branched on, and never written to logs or the audit record (threat T7); the audit fact
/// records only identifiers and the generic <c>Entity</c> kind name.
///
/// ATOMICITY. The entity insert and the audit append run inside a single explicit
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so
/// either the entity and its audit record commit together or a failure rolls both back leaving nothing
/// behind. The transaction is opened inside the EF execution strategy's <c>ExecuteAsync</c>, so it stays
/// correct under the CORE-CONC-003 retrying strategy.
/// </summary>
internal sealed class EntityCreationService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IEntityTypeRepository _entityTypes;
    private readonly IEntityRepository _entities;
    private readonly IAuditLogRepository _audit;

    public EntityCreationService(
        TransactionalUnitOfWork unitOfWork,
        IEntityTypeRepository entityTypes,
        IEntityRepository entities,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(entityTypes);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _entityTypes = entityTypes;
        _entities = entities;
        _audit = audit;
    }

    /// <summary>
    /// Creates a new entity in the given tenant and workspace as an instance of
    /// <paramref name="entityTypeId"/>, with the given name and attribute values, and appends an
    /// append-only <see cref="AuditAction.EntityCreated"/> record — atomically. Returns
    /// <see cref="EntityCreationResult.Created"/> carrying the created entity when the type resolved within
    /// the (organization, workspace), or <see cref="EntityCreationResult.UnknownEntityType"/> when no such
    /// type exists there (an unknown id, or one belonging to another workspace/tenant — nothing is created).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the entity is authored into.</param>
    /// <param name="entityTypeId">The entity type the new entity is an instance of (resolved within the workspace).</param>
    /// <param name="name">The entity's human label (template-/host-supplied DATA; validated for shape only).</param>
    /// <param name="attributeValues">The entity's attribute values (well-formed JSON DATA; validated for shape only).</param>
    /// <param name="actorUserProfileId">
    /// The authenticated authoring role who created the entity — the audited actor. Supplied by the endpoint
    /// from the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the entity's created/updated time and the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, entity type id or actor id is empty, or the name/attribute values
    /// violate an <see cref="Entity"/> invariant (the endpoint validates these before calling, so a throw
    /// here is defensive).
    /// </exception>
    public async Task<EntityCreationResult> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name,
        string attributeValues,
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

        if (entityTypeId == Guid.Empty)
        {
            throw new ArgumentException("Entity type id must not be empty.", nameof(entityTypeId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // SAME-WORKSPACE-TYPE coupling: resolve the entity type WITHIN the resolved tenant AND workspace.
        // FindByIdAsync leads with organization_id then workspace_id then the type id, so a type in another
        // workspace or tenant is never returned even when the surrogate id matches; an unknown id is simply
        // null. An unresolved type creates nothing and opens no transaction — the create is rejected as
        // UnknownEntityType (threats T1/T5).
        var entityType = await _entityTypes
            .FindByIdAsync(organizationId, workspaceId, entityTypeId, cancellationToken)
            .ConfigureAwait(false);
        if (entityType is null)
        {
            return EntityCreationResult.UnknownEntityType;
        }

        // ONE unit of work (CORE-CONC-002): the entity insert and the audit append commit together or roll
        // back together, so a creation is applied whole or not at all. Both repositories write through the
        // same scoped DbContext the TransactionalUnitOfWork begins the transaction on, so each SaveChanges
        // enrols in this transaction. The transaction is opened inside the EF execution strategy's
        // ExecuteAsync, so it stays correct under the CORE-CONC-003 retrying strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The aggregate mints the server-side surrogate id (UUIDv7) and fixes the tenant, workspace
                // and type immutably; the name and attribute values are stored as DATA (the template
                // boundary, docs/04). The endpoint validated the name/values, so Create does not throw here.
                var entity = Entity.Create(organizationId, workspaceId, entityTypeId, name, attributeValues, now);
                await _entities.AddAsync(entity, transactionCancellationToken).ConfigureAwait(false);

                // AUDIT (the story's "audited" criterion): a creation is security-relevant, so append an
                // append-only record capturing the actor (the authoring role who created the entity), the
                // created entity and the tenant/workspace. The entity's name and attribute values are NEVER
                // recorded — only identifiers and the generic Entity kind name (threat T7).
                var entry = AuditLogEntry.ForEntityCreation(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    nameof(Entity),
                    entity.Id,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return EntityCreationResult.Created(entity);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
