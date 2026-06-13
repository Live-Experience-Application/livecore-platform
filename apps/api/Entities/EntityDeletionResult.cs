namespace LiveCore.Api.Entities;

/// <summary>
/// Outcome of the entity deletion command (CORE-LIFE-003, the "Resource Lifecycle and Deletion" epic),
/// returned by <see cref="EntityDeletionService.DeleteAsync"/>.
///
/// The deletion is addressed by a workspace-scoped entity id, so the only two outcomes are that the
/// entity existed in the resolved tenant and workspace and was deleted (<see cref="Deleted"/>), or that
/// no such entity exists there (<see cref="NotFound"/>). The endpoint maps <see cref="NotFound"/> to a
/// safe hidden-404 (an unknown id, or an id belonging to another workspace/tenant, reveals nothing and
/// changes nothing) and <see cref="Deleted"/> to <c>204 No Content</c>. There is deliberately no
/// "blocked" outcome: the deletion CASCADES its dependents rather than blocking on them
/// (docs/adr/0012-resource-deletion-cascades-dependents.md).
/// </summary>
public enum EntityDeletionResult
{
    /// <summary>
    /// The entity existed within the resolved tenant and workspace and was hard-deleted, together with
    /// its dependent relationship edges, visibility rules and asset links, and the deletion was appended
    /// to the append-only audit log.
    /// </summary>
    Deleted = 1,

    /// <summary>
    /// No entity with the given id exists within the resolved tenant and workspace (an unknown id, or an
    /// id belonging to another workspace/tenant). Nothing was changed; the endpoint hides this as a safe
    /// 404 (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
