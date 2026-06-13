namespace LiveCore.Api.Content;

/// <summary>
/// Outcome of the content block deletion command (CORE-LIFE-004, the "Resource Lifecycle and Deletion"
/// epic), returned by <see cref="ContentBlockDeletionService.DeleteAsync"/>.
///
/// The deletion is addressed by a scene-scoped content block id, so the only two outcomes are that the
/// content block existed in the resolved tenant, workspace and scene and was deleted
/// (<see cref="Deleted"/>), or that no such content block exists there (<see cref="NotFound"/>). The
/// endpoint maps <see cref="NotFound"/> to a safe hidden-404 (an unknown id, or an id belonging to another
/// scene/workspace/tenant, reveals nothing and changes nothing) and <see cref="Deleted"/> to
/// <c>204 No Content</c>. There is deliberately no "blocked" outcome: the deletion CASCADES its dependents
/// rather than blocking on them, consistently with the entity deletion
/// (docs/adr/0012-resource-deletion-cascades-dependents.md).
/// </summary>
public enum ContentBlockDeletionResult
{
    /// <summary>
    /// The content block existed within the resolved tenant, workspace and scene and was hard-deleted,
    /// together with its inline revision history and its dependent visibility rules and asset links, and the
    /// deletion was appended to the append-only audit log.
    /// </summary>
    Deleted = 1,

    /// <summary>
    /// No content block with the given id exists within the resolved tenant, workspace and scene (an unknown
    /// id, or an id belonging to another scene/workspace/tenant). Nothing was changed; the endpoint hides
    /// this as a safe 404 (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
