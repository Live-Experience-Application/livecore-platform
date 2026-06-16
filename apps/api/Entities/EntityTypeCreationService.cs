using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Entities;

/// <summary>
/// The entity-type create command of the Entities module (CORE-ENT-007, the "Vertical Authoring and Read API
/// Completeness" epic). An authoring role can DEFINE a new generic <see cref="EntityType"/> in a workspace —
/// the DATA-DRIVEN definition (template key, display name and field/type metadata) through which a vertical
/// maps its domain onto Core (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). This service creates the
/// type with a SERVER-MINTED surrogate id, persists it and appends an append-only
/// <see cref="AuditAction.EntityTypeCreated"/> audit record — the create + audit committing together in ONE
/// database transaction so a definition is recorded as a fact or it does not happen (the story's
/// "tenant/workspace-scoped; audited" criterion). It is the type-definition counterpart of
/// <see cref="EntityCreationService"/>.
///
/// PER-WORKSPACE UNIQUE KEY. An entity type's <see cref="EntityType.TypeKey"/> is unique WITHIN its workspace
/// (a workspace cannot define the same type twice), enforced by the unique
/// (<c>organization_id</c>, <c>workspace_id</c>, <c>type_key</c>) index. The service resolves any existing key
/// through the tenant- AND workspace-scoped <see cref="IEntityTypeRepository.FindByKeyAsync"/> FIRST: a key
/// already defined in the caller's own workspace yields <see cref="EntityTypeCreationResult.DuplicateKey"/> —
/// nothing is created and no transaction is opened (mirroring how <see cref="EntityCreationService"/> resolves
/// the referenced type first and returns without opening a transaction). The same key is still available to
/// OTHER workspaces and tenants (threat T5); the unique index remains the real race guard, so a key claimed
/// concurrently after the pre-check still resolves to <see cref="EntityTypeCreationResult.DuplicateKey"/>
/// inside the transaction.
///
/// SCOPE / ISOLATION. The service takes the already-resolved tenant and workspace (the endpoint performed the
/// authentication, tenant resolution and role authorization before calling in; this service is the authorized
/// command's effect). The created entity type is bound to exactly that (organization, workspace) — the
/// <see cref="EntityType"/> aggregate fixes them immutably at construction — so it can never be authored into
/// another tenant or workspace (threat T5).
///
/// CONTENT BOUNDARY (the template boundary, docs/04_PRODUCT_BOUNDARIES.md). The type's
/// <see cref="EntityType.TypeKey"/>, <see cref="EntityType.DisplayName"/> and
/// <see cref="EntityType.AttributeSchema"/> are template-/host-supplied DATA, validated only for shape (a valid
/// slug key, a bounded display name, well-formed bounded JSON) by the aggregate — never inspected for
/// vocabulary, never branched on, and never written to logs or the audit record (threat T7); the audit fact
/// records only identifiers and the generic <c>EntityType</c> kind name.
///
/// ATOMICITY. The entity-type insert and the audit append run inside a single explicit
/// <see cref="Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"/> over the shared
/// <see cref="LiveCoreDbContext"/> (the same scoped context every injected repository writes through), so
/// either the type and its audit record commit together or a failure rolls both back leaving nothing behind.
/// The transaction is opened inside the EF execution strategy's <c>ExecuteAsync</c>, so it stays correct under
/// the CORE-CONC-003 retrying strategy.
/// </summary>
internal sealed class EntityTypeCreationService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IEntityTypeRepository _entityTypes;
    private readonly IAuditLogRepository _audit;

    public EntityTypeCreationService(
        TransactionalUnitOfWork unitOfWork,
        IEntityTypeRepository entityTypes,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(entityTypes);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _entityTypes = entityTypes;
        _audit = audit;
    }

    /// <summary>
    /// Defines a new entity type in the given tenant and workspace with the given key, display name and
    /// attribute schema, and appends an append-only <see cref="AuditAction.EntityTypeCreated"/> record —
    /// atomically. Returns <see cref="EntityTypeCreationResult.Created"/> carrying the created type when the
    /// key was free in the (organization, workspace), or <see cref="EntityTypeCreationResult.DuplicateKey"/>
    /// when the workspace already defines a type with that key (nothing is created).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the entity type is defined in.</param>
    /// <param name="typeKey">The type's canonical natural key (template-/host-supplied DATA; canonicalized and validated by the aggregate).</param>
    /// <param name="displayName">The type's human label (template-/host-supplied DATA; validated for shape only).</param>
    /// <param name="attributeSchema">The type's template-defined attribute schema (well-formed JSON DATA; validated for shape only).</param>
    /// <param name="actorUserProfileId">
    /// The authenticated authoring role who defined the type — the audited actor. Supplied by the endpoint from
    /// the resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="now">The command timestamp (the type's created/updated time and the audit record's time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or actor id is empty, or the key/display name/attribute schema violate
    /// an <see cref="EntityType"/> invariant (the endpoint validates these before calling, so a throw here is
    /// defensive).
    /// </exception>
    public async Task<EntityTypeCreationResult> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        string displayName,
        string attributeSchema,
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

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        // PER-WORKSPACE UNIQUE KEY: resolve any existing type with this key WITHIN the resolved tenant AND
        // workspace FIRST. FindByKeyAsync canonicalizes the key and leads with organization_id then
        // workspace_id, so the same key in another workspace or tenant is never matched (threat T5). An already
        // defined key creates nothing and opens no transaction — the create is rejected as DuplicateKey. The
        // unique index is still the real race guard for a key claimed concurrently after this pre-check (handled
        // below).
        var existing = await _entityTypes
            .FindByKeyAsync(organizationId, workspaceId, typeKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return EntityTypeCreationResult.DuplicateKey;
        }

        // ONE unit of work (CORE-CONC-002): the entity-type insert and the audit append commit together or roll
        // back together, so a definition is applied whole or not at all. Both repositories write through the
        // same scoped DbContext the TransactionalUnitOfWork begins the transaction on, so each SaveChanges
        // enrols in this transaction. The transaction is opened inside the EF execution strategy's ExecuteAsync,
        // so it stays correct under the CORE-CONC-003 retrying strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The aggregate mints the server-side surrogate id (UUIDv7) and fixes the tenant and workspace
                // immutably; the key, display name and attribute schema are stored as DATA (the template
                // boundary, docs/04). The endpoint validated them, so Create does not throw here.
                var entityType = EntityType.Create(
                    organizationId, workspaceId, typeKey, displayName, attributeSchema, now);

                // The unique (organization_id, workspace_id, type_key) index is the authoritative race guard: a
                // key claimed concurrently between the pre-check and here is rejected as a duplicate, so nothing
                // is created or audited and the existing type is left unchanged.
                var addResult = await _entityTypes
                    .AddAsync(entityType, transactionCancellationToken)
                    .ConfigureAwait(false);
                if (addResult == EntityTypeAddResult.DuplicateKey)
                {
                    return EntityTypeCreationResult.DuplicateKey;
                }

                // AUDIT (the story's "audited" criterion): a definition is security-relevant, so append an
                // append-only record capturing the actor (the authoring role who defined the type), the created
                // type and the tenant/workspace. The type's key, display name and attribute schema are NEVER
                // recorded — only identifiers and the generic EntityType kind name (threat T7).
                var entry = AuditLogEntry.ForEntityTypeCreation(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    nameof(EntityType),
                    entityType.Id,
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return EntityTypeCreationResult.Created(entityType);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
