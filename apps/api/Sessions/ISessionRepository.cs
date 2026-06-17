// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
    /// Lists ONE BOUNDED PAGE of the given workspace's sessions (owned by the given organization) in the same
    /// deterministic, tenant- AND workspace-scoped order as <see cref="ListByWorkspaceAsync"/> — by the
    /// time-ordered surrogate id (UUIDv7) — but limited to <paramref name="take"/> rows starting at
    /// <paramref name="skip"/>, backing the bounded workspace session list route
    /// (<c>GET /api/v1/workspaces/{workspaceId}/sessions</c>, CORE-DX-003). The page is exactly tenant- and
    /// workspace-scoped (the predicate leads with <c>organization_id</c> then matches <c>workspace_id</c>), so a
    /// foreign tenant's or workspace's sessions are NEVER returned (threat T5/T1). The caller over-fetches one
    /// extra row (<c>take = limit + 1</c>) to decide <c>hasMore</c> without a second COUNT, mirroring the audit
    /// page. Bounding the read means a single list response can never materialize the whole table (threat T9).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<Session>> ListPageByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        int skip,
        int take,
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

    /// <summary>
    /// Lists up to <paramref name="maxCount"/> TERMINAL sessions (<see cref="SessionStatus.Ended"/> or
    /// <see cref="SessionStatus.Cancelled"/>) created before <paramref name="createdBefore"/> — the candidates
    /// for the data-retention sweep (CORE-PRIV-003, GDPR Art.5(1)(e) storage limitation). This is a SYSTEM
    /// maintenance read that deliberately spans ALL tenants and workspaces (the retention sweep is a system job,
    /// not a tenant actor) and returns only TERMINAL sessions, so a live or still-prepared session is never a
    /// candidate. The retention window is measured from the session's CREATION time (its age); ordering is by the
    /// time-ordered surrogate id (UUIDv7, derived from the creation time), oldest first, and the creation-age
    /// threshold is applied after materialization, so the bounded batch surfaces the oldest terminal sessions and
    /// over repeated sweeps every past-window terminal session is covered. The caller (the retention sweep) then
    /// re-loads and purges each within its own transaction, so this read takes no lock and grants no access.
    /// </summary>
    /// <param name="createdBefore">The exclusive creation-time threshold; only sessions created before it are returned.</param>
    /// <param name="maxCount">The maximum number of sessions a single sweep examines (a positive batch size).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    Task<IReadOnlyList<Session>> ListTerminalForRetentionAsync(
        DateTimeOffset createdBefore,
        int maxCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES a terminal session row as part of the data-retention sweep (CORE-PRIV-003). The schema's
    /// <c>ON DELETE CASCADE</c> foreign keys then remove the session's append-only <c>session_events</c> (and
    /// their per-session sequence counter), its <c>recaps</c> and its session-scoped <c>visibility_rules</c>
    /// (docs/10_DATABASE_SCHEMA.md) — the "completed/expired sessions and their session events" purge. The
    /// <c>audit_logs</c> references the session as a recorded fact, not a foreign key, so the audit trail
    /// SURVIVES the purge. The caller must have re-loaded the session through a tenant-scoped lookup inside the
    /// purge transaction; a concurrent purge of the same row surfaces as a
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> (zero rows affected) which the
    /// sweep treats as already-handled (concurrency-safe).
    /// </summary>
    Task DeleteAsync(Session session, CancellationToken cancellationToken);
}
