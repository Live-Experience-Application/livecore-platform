namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of the entity-type create command (CORE-ENT-007, the "Vertical Authoring and Read API Completeness"
/// epic), returned by <see cref="EntityTypeCreationService.CreateAsync"/>.
///
/// An entity type has a per-workspace natural key (<see cref="EntityType.TypeKey"/>), so a definition is gated
/// by per-workspace key uniqueness the database enforces with a unique
/// (<c>organization_id</c>, <c>workspace_id</c>, <c>type_key</c>) index. So there are exactly two outcomes —
/// the key was free in the caller's workspace and a new type was defined and audited
/// (<see cref="Status"/> is <see cref="EntityTypeCreationStatus.Created"/>, carrying the created
/// <see cref="EntityType"/>), or the workspace already defines a type with that key
/// (<see cref="EntityTypeCreationStatus.DuplicateKey"/>, so nothing is created and no audit fact is written;
/// the existing type is left unchanged). The endpoint maps
/// <see cref="EntityTypeCreationStatus.DuplicateKey"/> to a <c>409 Conflict</c> (the workspace already defines
/// the key; the same key stays available to other workspaces, threat T5) and
/// <see cref="EntityTypeCreationStatus.Created"/> to a <c>201 Created</c>.
/// </summary>
internal readonly record struct EntityTypeCreationResult
{
    private EntityTypeCreationResult(EntityTypeCreationStatus status, EntityType? entityType)
    {
        Status = status;
        EntityType = entityType;
    }

    /// <summary>The outcome kind.</summary>
    public EntityTypeCreationStatus Status { get; }

    /// <summary>
    /// The created entity type when <see cref="Status"/> is <see cref="EntityTypeCreationStatus.Created"/>;
    /// otherwise <see langword="null"/> (no entity type was created).
    /// </summary>
    public EntityType? EntityType { get; }

    /// <summary>The key was free in the workspace, so the entity type was created and audited.</summary>
    public static EntityTypeCreationResult Created(EntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return new EntityTypeCreationResult(EntityTypeCreationStatus.Created, entityType);
    }

    /// <summary>
    /// An entity type with the given key already exists in the resolved tenant and workspace, so nothing was
    /// created (the per-workspace unique natural key).
    /// </summary>
    public static EntityTypeCreationResult DuplicateKey { get; } =
        new(EntityTypeCreationStatus.DuplicateKey, null);
}

/// <summary>The kind of <see cref="EntityTypeCreationResult"/>.</summary>
internal enum EntityTypeCreationStatus
{
    /// <summary>A new entity type was created, persisted and appended to the append-only audit log.</summary>
    Created = 1,

    /// <summary>
    /// The workspace already defines an entity type with the requested key, so no entity type was created (the
    /// per-workspace unique (<c>workspace_id</c>, <c>type_key</c>) natural key).
    /// </summary>
    DuplicateKey = 2,
}
