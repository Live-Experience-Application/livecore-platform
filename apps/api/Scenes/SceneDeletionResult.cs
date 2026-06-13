namespace LiveCore.Api.Scenes;

/// <summary>
/// Outcome of the scene deletion command (CORE-LIFE-005, the "Resource Lifecycle and Deletion" epic),
/// returned by <see cref="SceneDeletionService.DeleteAsync"/>.
///
/// The deletion is addressed by a workspace-scoped scene id, so the only two outcomes are that the scene
/// existed in the resolved tenant and workspace and was deleted (<see cref="Deleted"/>), or that no such
/// scene exists there (<see cref="NotFound"/>). The endpoint maps <see cref="NotFound"/> to a safe
/// hidden-404 (an unknown id, or an id belonging to another workspace/tenant, reveals nothing and changes
/// nothing) and <see cref="Deleted"/> to <c>204 No Content</c>. There is deliberately no "blocked"
/// outcome: the deletion CASCADES its dependents (its child content blocks and every governing visibility
/// rule / asset link) rather than blocking on them, consistently with the entity and content-block
/// deletions (docs/adr/0012-resource-deletion-cascades-dependents.md).
/// </summary>
public enum SceneDeletionResult
{
    /// <summary>
    /// The scene existed within the resolved tenant and workspace and was hard-deleted, together with its
    /// child content blocks (and their inline revision history) and the dependent visibility rules and asset
    /// links of both the scene and those content blocks; the remaining scenes re-packed their ordering, and
    /// the deletion was appended to the append-only audit log.
    /// </summary>
    Deleted = 1,

    /// <summary>
    /// No scene with the given id exists within the resolved tenant and workspace (an unknown id, or an id
    /// belonging to another workspace/tenant). Nothing was changed; the endpoint hides this as a safe 404
    /// (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
