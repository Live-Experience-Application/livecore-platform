// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of the entity-relationship create command (CORE-ENT-008, the "Entity Graph and Search
/// Completeness" epic), returned by <see cref="EntityRelationshipCreationService.CreateAsync"/>.
///
/// A create is gated by the SAME-WORKSPACE-ENDPOINTS coupling that the database foreign keys cannot enforce
/// (<see cref="EntityRelationship"/>): BOTH the source and the target entity must exist IN THE CALLER'S OWN
/// (organization, workspace). So there are four outcomes — the edge was created
/// (<see cref="EntityRelationshipCreationStatus.Created"/>, carrying the created
/// <see cref="EntityRelationship"/>); the source endpoint does not resolve within the scope
/// (<see cref="EntityRelationshipCreationStatus.UnknownSourceEntity"/>); the target endpoint does not resolve
/// within the scope (<see cref="EntityRelationshipCreationStatus.UnknownTargetEntity"/>); or the workspace
/// already holds the same directed edge of the same kind
/// (<see cref="EntityRelationshipCreationStatus.Duplicate"/>). The endpoint maps the two unresolved-endpoint
/// outcomes to a <c>400</c> (the body-supplied endpoint reference does not resolve in the caller's authorized
/// workspace; an unknown id and one belonging to another workspace/tenant are indistinguishable, so nothing
/// is created and the response leaks nothing, threats T1/T5), <see cref="EntityRelationshipCreationStatus.Duplicate"/>
/// to a <c>409</c> and <see cref="EntityRelationshipCreationStatus.Created"/> to a <c>201 Created</c>.
/// </summary>
internal readonly record struct EntityRelationshipCreationResult
{
    private EntityRelationshipCreationResult(EntityRelationshipCreationStatus status, EntityRelationship? relationship)
    {
        Status = status;
        Relationship = relationship;
    }

    /// <summary>The outcome kind.</summary>
    public EntityRelationshipCreationStatus Status { get; }

    /// <summary>
    /// The created relationship when <see cref="Status"/> is
    /// <see cref="EntityRelationshipCreationStatus.Created"/>; otherwise <see langword="null"/> (no edge was
    /// created).
    /// </summary>
    public EntityRelationship? Relationship { get; }

    /// <summary>Both endpoints resolved within the workspace and a new directed edge was created.</summary>
    public static EntityRelationshipCreationResult Created(EntityRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return new EntityRelationshipCreationResult(EntityRelationshipCreationStatus.Created, relationship);
    }

    /// <summary>
    /// The SOURCE endpoint does not resolve within the resolved tenant and workspace (an unknown id, or an id
    /// belonging to another workspace/tenant). Nothing was created.
    /// </summary>
    public static EntityRelationshipCreationResult UnknownSourceEntity { get; } =
        new(EntityRelationshipCreationStatus.UnknownSourceEntity, null);

    /// <summary>
    /// The TARGET endpoint does not resolve within the resolved tenant and workspace (an unknown id, or an id
    /// belonging to another workspace/tenant). Nothing was created.
    /// </summary>
    public static EntityRelationshipCreationResult UnknownTargetEntity { get; } =
        new(EntityRelationshipCreationStatus.UnknownTargetEntity, null);

    /// <summary>
    /// The workspace already holds the same directed edge of the same kind (the per-workspace unique
    /// (<c>workspace_id</c>, <c>source_entity_id</c>, <c>target_entity_id</c>, <c>relationship_kind</c>)
    /// index rejected the insert). The existing edge is unchanged and nothing new was created.
    /// </summary>
    public static EntityRelationshipCreationResult Duplicate { get; } =
        new(EntityRelationshipCreationStatus.Duplicate, null);
}

/// <summary>The kind of <see cref="EntityRelationshipCreationResult"/>.</summary>
internal enum EntityRelationshipCreationStatus
{
    /// <summary>A new directed edge was created and persisted.</summary>
    Created = 1,

    /// <summary>
    /// The referenced SOURCE entity does not exist within the resolved tenant and workspace, so no edge was
    /// created (the same-workspace-endpoints coupling the database foreign key cannot enforce).
    /// </summary>
    UnknownSourceEntity = 2,

    /// <summary>
    /// The referenced TARGET entity does not exist within the resolved tenant and workspace, so no edge was
    /// created (the same-workspace-endpoints coupling the database foreign key cannot enforce).
    /// </summary>
    UnknownTargetEntity = 3,

    /// <summary>
    /// The workspace already holds the same directed edge of the same kind, so no edge was created (the
    /// per-workspace unique natural key rejected the duplicate insert).
    /// </summary>
    Duplicate = 4,
}
