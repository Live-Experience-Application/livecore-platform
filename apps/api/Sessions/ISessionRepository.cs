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
/// authorization). The workspace-scoped <see cref="ListByWorkspaceAsync"/> (added
/// for the session create/list API, CORE-API-003) is the one list method; it is
/// also tenant- AND workspace-scoped and never crosses either boundary. Resolving
/// the "current" organization or workspace from a request is not done here; that is
/// the tenant context resolver (CORE-ID-005) and the endpoint layer. This contract
/// takes explicit ids.
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
    /// Finds the session with exactly the given id WITHIN the given organization
    /// (tenant) ALONE — without a workspace boundary — or <see langword="null"/>
    /// when no such session exists in that organization (CORE-SES-004).
    ///
    /// This lookup exists because the lifecycle command routes
    /// (<c>POST /api/v1/sessions/{sessionId}/start</c> and <c>.../end</c>) address
    /// a session by id within a query-supplied organization only: the route path
    /// carries no workspace, so the workspace boundary is not known up front. The
    /// session's own <see cref="Session.WorkspaceId"/> is DISCOVERED from the
    /// returned row AFTER the tenant boundary has been enforced, and the caller
    /// then authorizes against WORKSPACE membership in exactly that workspace. The
    /// lookup is still tenant-safe: the predicate leads with
    /// <c>organization_id</c>, so a session in another organization is NEVER
    /// returned even when the surrogate id matches, and the surrogate id alone
    /// never crosses the tenant boundary (threat T5 in
    /// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
    /// authorization; docs/06_AUTHORIZATION_MATRIX.md: the organization boundary is
    /// checked before the workspace boundary). It returns the whole session,
    /// carrying its workspace id, so the subsequent workspace-membership check has
    /// the workspace it needs.
    ///
    /// The two-boundary <see cref="FindByIdAsync(Guid, Guid, Guid, CancellationToken)"/>
    /// REMAINS the workspace-scoped lookup and is the right choice whenever the
    /// workspace is already known from the request (for example the workspace
    /// session routes). This org-only lookup is used only by the by-session-id
    /// routes, where the workspace is not in the path and must be discovered from
    /// the row inside the tenant boundary.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or session id is empty. An empty id can never address a
    /// stored session, so the lookup is rejected instead of silently returning
    /// nothing.
    /// </exception>
    Task<Session?> FindByIdInOrganizationAsync(
        Guid organizationId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every session of the given workspace (owned by the given organization)
    /// in a deterministic order — by the time-ordered surrogate id (UUIDv7), which is
    /// chronological and provider-independent — backing the workspace session list
    /// route (<c>GET /api/v1/workspaces/{workspaceId}/sessions</c>, CORE-API-003).
    ///
    /// The list is tenant- AND workspace-scoped: the predicate leads with
    /// <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign
    /// tenant's or a foreign workspace's sessions are NEVER returned even when their
    /// ids would otherwise be addressable (threat T5/T1; docs/06_AUTHORIZATION_MATRIX.md:
    /// the organization boundary is checked before the workspace boundary). An empty
    /// list is returned for a workspace that has no sessions; the lookup never crosses
    /// the tenant or workspace boundary to borrow another workspace's sessions. Every
    /// lifecycle status is returned (the list is not filtered by status); deciding what
    /// each role sees is the endpoint's concern, not the repository's.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty. An empty id can never address a
    /// stored workspace's sessions, so the lookup is rejected instead of silently
    /// returning nothing.
    /// </exception>
    Task<IReadOnlyList<Session>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
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
