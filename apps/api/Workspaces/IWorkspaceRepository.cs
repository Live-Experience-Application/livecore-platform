// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
    /// Lists the ACTIVE workspaces in the given organization that the given subject
    /// is a member of (CORE-WS-003: <c>GET /api/v1/workspaces</c>, "Filtered by
    /// membership"). The lookup is scoped to exactly one organization and joins
    /// the <c>workspace_members</c> table, so it returns only the workspaces in
    /// that tenant for which a membership row exists for the subject: a workspace
    /// the subject is not a member of, and any workspace in another organization,
    /// are never returned (deny-by-default; threat T5 in
    /// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level
    /// authorization). ARCHIVED workspaces are excluded: this is the active list,
    /// and an archived workspace is "excluded from active lists" (CORE-LIFE-009) —
    /// it remains reachable through the by-id read, which is read-only. Results are
    /// ordered by the workspace surrogate id (UUIDv7, time-ordered) so the listing
    /// is stable. An empty list (the subject is a member of no active workspace in
    /// that tenant) is a normal, non-error result.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or subject id is empty. An empty id can never address
    /// a stored tenant or subject, so the lookup is rejected instead of silently
    /// returning nothing.
    /// </exception>
    Task<IReadOnlyList<Workspace>> ListByMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the ACTIVE workspaces in the given organization that the given subject is a
    /// member of, in the same deterministic (UUIDv7 id) order as <see cref="ListByMemberAsync"/>, limited to
    /// <paramref name="take"/> rows starting at <paramref name="skip"/>, backing the bounded workspace list
    /// route (<c>GET /api/v1/workspaces</c>, CORE-DX-003). It applies the same deny-by-default scoping
    /// (organization AND a membership row for the subject; archived workspaces excluded), so a workspace the
    /// subject is not a member of, and any workspace in another organization, are never returned (threat T5/T1).
    /// The caller over-fetches one extra row (<c>take = limit + 1</c>) to decide <c>hasMore</c> without a second
    /// COUNT. Bounding the read means a single list response can never materialize an unbounded array (threat T9).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or subject id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<Workspace>> ListPageByMemberAsync(
        Guid organizationId,
        Guid userProfileId,
        int skip,
        int take,
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

    /// <summary>
    /// Persists changes to a workspace previously loaded through this repository.
    /// The organization, slug and id of a workspace are immutable
    /// (<see cref="Workspace"/>), so an update only ever changes the display name
    /// and the update timestamp; it can never move the workspace to another
    /// tenant (threat T5). The caller is responsible for having loaded the
    /// workspace through a tenant-scoped lookup.
    /// </summary>
    Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken);
}
