namespace LiveCore.Api.Workspaces;

/// <summary>
/// Persistence contract for the workspace aggregate (CORE-WS-001). The
/// Workspaces module owns the <c>workspaces</c> table; other modules access
/// workspaces only through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Workspaces module owns workspace metadata).
///
/// Every lookup is explicitly tenant-scoped: the caller passes the organization
/// id together with the workspace id or slug, and a workspace is only ever
/// returned when it belongs to exactly that organization. There is deliberately
/// no lookup of a workspace by id or slug alone and no lookup that crosses
/// tenants, so one organization's workspace can never be read through another
/// organization's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1
/// broken object-level authorization). This is the aggregate + persistence
/// story; HTTP endpoints and the per-action authorization policy that produces
/// and consumes the tenant context (CORE-ID-005) are later stories (the
/// workspace create/read/update API CORE-WS-003 and the authorization policy
/// tests CORE-WS-005) and are deliberately not built here. This contract takes
/// explicit ids.
/// </summary>
public interface IWorkspaceRepository
{
    /// <summary>
    /// Finds the workspace with exactly the given id WITHIN the given
    /// organization, or <see langword="null"/> when no such workspace exists in
    /// that organization. The organization scopes the lookup, so a workspace
    /// that exists under another organization's id is never returned, even when
    /// the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never
    /// address a stored workspace, so the lookup is rejected instead of
    /// silently returning nothing.
    /// </exception>
    Task<Workspace?> FindByIdAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the workspace with exactly the given canonical slug WITHIN the
    /// given organization, or <see langword="null"/> when no such workspace
    /// exists in that organization. The slug is canonicalized before matching so
    /// callers may pass any casing/whitespace variant of a valid slug; matching
    /// against stored values is then exact (ordinal, case-sensitive). Because
    /// the slug is unique only per organization, the organization is required to
    /// disambiguate: the same slug under another organization is never returned
    /// (threat T5).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id is empty, or the slug violates the slug invariants.
    /// An empty organization id or an invalid slug can never address a stored
    /// workspace, so the lookup is rejected instead of silently returning
    /// nothing.
    /// </exception>
    Task<Workspace?> FindBySlugAsync(
        Guid organizationId,
        string slug,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new workspace. Returns
    /// <see cref="WorkspaceAddResult.DuplicateSlug"/> when a workspace with the
    /// same slug already exists in the same organization (enforced by the unique
    /// (organization_id, slug) database index, so concurrent first-time callers
    /// can never create two workspaces with one slug in one tenant). The same
    /// slug remains available to other organizations.
    /// </summary>
    Task<WorkspaceAddResult> AddAsync(Workspace workspace, CancellationToken cancellationToken);
}
