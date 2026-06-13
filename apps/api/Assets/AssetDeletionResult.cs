namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the host-initiated asset deletion command (CORE-LIFE-006, the "Resource Lifecycle and
/// Deletion" epic), returned by <see cref="AssetDeletionService.DeleteAsync"/>.
///
/// The deletion is addressed by a tenant- and workspace-scoped asset id, so the only two outcomes are that
/// the asset existed in the resolved tenant and workspace and was deleted (<see cref="Deleted"/>), or that
/// no such asset exists there (<see cref="NotFound"/>). The endpoint maps <see cref="NotFound"/> to a safe
/// hidden-404 (an unknown id, or an id belonging to another workspace/tenant, reveals nothing and changes
/// nothing) and <see cref="Deleted"/> to <c>204 No Content</c>. There is deliberately no "blocked" outcome:
/// the deletion CASCADES its links rather than blocking on them, consistently with the entity, content-block
/// and scene deletions (docs/adr/0012-resource-deletion-cascades-dependents.md).
///
/// A storage-unconfigured failure is NOT a result value: <see cref="AssetDeletionService.DeleteAsync"/>
/// deletes the underlying storage object BEFORE the metadata row, so when no object storage is configured the
/// fail-closed <see cref="UnconfiguredAssetStorage"/> throws <see cref="AssetStorageNotConfiguredException"/>
/// and the whole transaction rolls back having changed nothing — the endpoint maps that to <c>503</c>, exactly
/// as the upload-intent flow does (the private-by-default posture holds even unconfigured; threat T4).
/// </summary>
public enum AssetDeletionResult
{
    /// <summary>
    /// The asset existed within the resolved tenant and workspace and was hard-deleted, together with its
    /// asset links and its underlying storage object, and the deletion was appended to the append-only audit
    /// log.
    /// </summary>
    Deleted = 1,

    /// <summary>
    /// No asset with the given id exists within the resolved tenant and workspace (an unknown id, or an id
    /// belonging to another workspace/tenant). Nothing was changed; the endpoint hides this as a safe 404
    /// (threats T1/T5).
    /// </summary>
    NotFound = 2,
}
