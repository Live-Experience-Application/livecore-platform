namespace LiveCore.Api.Organizations;

/// <summary>
/// Outcome of the authorized tenant organization deletion command
/// (<see cref="OrganizationDeletionService"/>, CORE-PRIV-002). The endpoint maps each outcome to a
/// fail-closed HTTP status without echoing any detail (threat T7).
/// </summary>
internal enum OrganizationDeletionResult
{
    /// <summary>
    /// The organization was deleted: the tenant root row was removed and the database's
    /// <c>ON DELETE CASCADE</c> foreign keys tore down the rest of the tenant (its workspaces, sessions,
    /// participants, memberships and its own audit log), and the offboarding was recorded as a platform-level
    /// audit fact that survives the teardown. The endpoint returns <c>204 No Content</c>.
    /// </summary>
    Deleted,

    /// <summary>
    /// No organization exists for the resolved id. The endpoint hides this as a <c>404</c> (it should not occur
    /// once a tenant context has been resolved, since the resolution proved the organization exists, but the
    /// command fails closed rather than assuming — for example a concurrent deletion of the same tenant).
    /// </summary>
    OrganizationNotFound,
}
