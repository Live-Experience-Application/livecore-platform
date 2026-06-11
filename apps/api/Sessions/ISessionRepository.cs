namespace LiveCore.Api.Sessions;

/// <summary>
/// Persistence contract for the session aggregate (CORE-SES-002). The Sessions
/// module owns the <c>sessions</c> table; other modules access sessions only
/// through this contract or the module's application services
/// (docs/02_ARCHITECTURE.md: no direct table ownership violations;
/// docs/05_MODULE_CONTRACTS.md: the Sessions module owns "session lifecycle" and
/// "session status").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the
/// organization id and the workspace id together with the session id, and a
/// session is only ever returned when it belongs to exactly that (organization,
/// workspace) pair. The organization boundary is checked before the workspace
/// boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a
/// session is never returned through a foreign organization's id even when the
/// workspace and session ids are correct, and never through a foreign workspace's
/// id even when the organization and session ids are correct. There is deliberately
/// no lookup of a session by id alone and no lookup that crosses tenants, so one
/// workspace's session can never be read through another workspace's id and a
/// session in one tenant can never be read through another tenant's id (threat T5
/// in docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
/// authorization). There is also no <c>ListBy*</c> method: the workspace session
/// list endpoint (GET /api/v1/workspaces/{workspaceId}/sessions) is a later story.
/// Resolving the "current" organization or workspace from a request is not done
/// here; that is the tenant context resolver (CORE-ID-005) and later endpoint
/// stories. This is the aggregate + persistence story; the create/start/end HTTP
/// endpoints and the <c>SessionCreated</c>/<c>Started</c>/<c>Ended</c> events
/// (CORE-SES-004) are later stories and are deliberately not built here. This
/// contract takes explicit ids.
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// Finds the session with exactly the given id WITHIN the given organization
    /// and workspace, or <see langword="null"/> when no such session exists there.
    /// The organization and workspace both scope the lookup, so a session that
    /// exists under another organization's or workspace's id is never returned, even
    /// when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or session id is empty. An empty id can
    /// never address a stored session, so the lookup is rejected instead of silently
    /// returning nothing.
    /// </exception>
    Task<Session?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new session. A session has no natural key (it is identified only
    /// by its surrogate id), so there is no uniqueness outcome to report; the result
    /// is always <see cref="SessionAddResult.Added"/> on success. Foreign-key
    /// violations (a non-existent workspace or tenant) surface as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<SessionAddResult> AddAsync(Session session, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a session previously loaded through this repository. The
    /// organization, workspace and id of a session are immutable
    /// (<see cref="Session"/>), so an update only ever changes the title, the
    /// lifecycle status, the live-timeline timestamps and the update timestamp; it
    /// can never move the session to another tenant or workspace (threat T5). The
    /// caller is responsible for having loaded the session through a tenant-scoped
    /// lookup.
    /// </summary>
    Task UpdateAsync(Session session, CancellationToken cancellationToken);
}
