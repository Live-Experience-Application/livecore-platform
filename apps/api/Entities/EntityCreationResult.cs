namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of the entity create command (CORE-ENT-006, the "Vertical Authoring and Read API Completeness"
/// epic), returned by <see cref="EntityCreationService.CreateAsync"/>.
///
/// A create is gated by the SAME-WORKSPACE-TYPE coupling that the database foreign key cannot enforce
/// (<see cref="Entity"/>): the referenced <see cref="EntityType"/> must exist IN THE CALLER'S OWN
/// (organization, workspace). So there are exactly two outcomes — the type resolved within that scope and a
/// new entity was created and audited (<see cref="Status"/> is <see cref="EntityCreationStatus.Created"/>,
/// carrying the created <see cref="Entity"/>), or no such type exists there
/// (<see cref="EntityCreationStatus.UnknownEntityType"/>, an unknown type id or one belonging to another
/// workspace/tenant — indistinguishable, so nothing is created and no audit fact is written). The endpoint
/// maps <see cref="EntityCreationStatus.UnknownEntityType"/> to a <c>400</c> (the body-supplied type
/// reference does not resolve in the caller's authorized workspace; the same response for unknown and
/// foreign types leaks nothing, threats T1/T5) and <see cref="EntityCreationStatus.Created"/> to a
/// <c>201 Created</c>.
/// </summary>
internal readonly record struct EntityCreationResult
{
    private EntityCreationResult(EntityCreationStatus status, Entity? entity)
    {
        Status = status;
        Entity = entity;
    }

    /// <summary>The outcome kind.</summary>
    public EntityCreationStatus Status { get; }

    /// <summary>
    /// The created entity when <see cref="Status"/> is <see cref="EntityCreationStatus.Created"/>; otherwise
    /// <see langword="null"/> (no entity was created).
    /// </summary>
    public Entity? Entity { get; }

    /// <summary>The entity resolved its type within the workspace and was created and audited.</summary>
    public static EntityCreationResult Created(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new EntityCreationResult(EntityCreationStatus.Created, entity);
    }

    /// <summary>
    /// No entity type with the given id exists within the resolved tenant and workspace (an unknown id, or an
    /// id belonging to another workspace/tenant). Nothing was created.
    /// </summary>
    public static EntityCreationResult UnknownEntityType { get; } =
        new(EntityCreationStatus.UnknownEntityType, null);
}

/// <summary>The kind of <see cref="EntityCreationResult"/>.</summary>
internal enum EntityCreationStatus
{
    /// <summary>A new entity was created, persisted and appended to the append-only audit log.</summary>
    Created = 1,

    /// <summary>
    /// The referenced entity type does not exist within the resolved tenant and workspace, so no entity was
    /// created (the same-workspace-type coupling the database foreign key cannot enforce).
    /// </summary>
    UnknownEntityType = 2,
}
