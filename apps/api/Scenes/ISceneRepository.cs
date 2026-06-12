namespace LiveCore.Api.Scenes;

/// <summary>
/// Persistence contract for the scene aggregate (CORE-SCENE-001). The Scenes module
/// owns the <c>scenes</c> table; other modules access scenes only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct
/// table ownership violations; docs/05_MODULE_CONTRACTS.md: the Scenes module owns
/// "scene metadata" and "scene ordering").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the
/// organization id and the workspace id, and a scene is only ever returned when it
/// belongs to exactly that (organization, workspace) pair. The organization boundary
/// is checked before the workspace boundary (docs/06_AUTHORIZATION_MATRIX.md
/// authorization principles), so a scene is never returned through a foreign
/// organization's id even when the workspace and scene ids are correct, and never
/// through a foreign workspace's id even when the organization and scene ids are
/// correct. There is deliberately no lookup of a scene by id alone and no lookup that
/// crosses tenants, so one workspace's scene can never be read through another
/// workspace's id and a scene in one tenant can never be read through another
/// tenant's id (threat T5 in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken
/// object-level authorization). Resolving the "current" organization or workspace
/// from a request is not done here; that is the tenant context resolver
/// (CORE-ID-005) and later endpoint stories. This is the aggregate + ordering +
/// persistence story; the create/reorder HTTP endpoints
/// (POST /api/v1/workspaces/{workspaceId}/scenes and the reorder route), content
/// blocks, host/participant DTO projection and content validation are LATER stories
/// (CORE-SCENE-002..005) and are deliberately not built here. This contract takes
/// explicit ids.
/// </summary>
public interface ISceneRepository
{
    /// <summary>
    /// Finds the scene with exactly the given id WITHIN the given organization and
    /// workspace, or <see langword="null"/> when no such scene exists there. The
    /// organization and workspace both scope the lookup, so a scene that exists under
    /// another organization's or workspace's id is never returned, even when the
    /// surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or scene id is empty. An empty id can never
    /// address a stored scene, so the lookup is rejected instead of silently returning
    /// nothing.
    /// </exception>
    Task<Scene?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every scene of the given workspace (owned by the given organization) in
    /// the deterministic ordering position order — sorted by the scene order, then by
    /// the surrogate id so ties never produce a nondeterministic sequence. This is the
    /// ordering feature (docs/05_MODULE_CONTRACTS.md: the Scenes module owns "scene
    /// ordering") made observable.
    ///
    /// The list is tenant- AND workspace-scoped: the predicate leads with
    /// <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign
    /// tenant's or a foreign workspace's scenes are NEVER returned even when their ids
    /// would otherwise be addressable (threat T5/T1; docs/06_AUTHORIZATION_MATRIX.md:
    /// the organization boundary is checked before the workspace boundary). An empty
    /// list is returned for a workspace that has no scenes; the lookup never crosses
    /// the tenant or workspace boundary to borrow another workspace's scenes.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a
    /// stored workspace's scenes, so the lookup is rejected instead of silently
    /// returning nothing.
    /// </exception>
    Task<IReadOnlyList<Scene>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new scene. A scene has no natural key and a deliberately non-unique
    /// order column (it is identified only by its surrogate id), so there is no
    /// uniqueness outcome to report; the result is always
    /// <see cref="SceneAddResult.Added"/> on success. Foreign-key violations (a
    /// non-existent workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<SceneAddResult> AddAsync(Scene scene, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a scene previously loaded through this repository. The
    /// organization, workspace and id of a scene are immutable (<see cref="Scene"/>),
    /// so an update only ever changes the title, the order and the update timestamp; it
    /// can never move the scene to another tenant or workspace (threat T5). The caller
    /// is responsible for having loaded the scene through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(Scene scene, CancellationToken cancellationToken);
}
