// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Visibility;

/// <summary>
/// Persistence contract for the visibility rule aggregate (CORE-VIS-001). The Visibility module owns
/// the <c>visibility_rules</c> table; other modules access visibility rules only through this
/// contract or the module's application services (docs/02_ARCHITECTURE.md: no direct table ownership
/// violations; docs/05_MODULE_CONTRACTS.md: the Visibility module owns "visibility rules" and is
/// "the central security module").
///
/// Every lookup is explicitly scoped by BOTH boundaries: the caller passes the organization id and
/// the workspace id, and a rule is only ever returned when it belongs to exactly that
/// (organization, workspace) pair. The organization boundary is checked before the workspace
/// boundary (docs/06_AUTHORIZATION_MATRIX.md authorization principles), so a rule is never returned
/// through a foreign organization's id even when the workspace and ids are correct, and never
/// through a foreign workspace's id even when the organization and ids are correct. There is
/// deliberately no lookup of a rule by id alone, no lookup that crosses tenants, and NO
/// list-everything method, so one workspace's rule can never be read through another workspace's id
/// and a rule in one tenant can never be read through another tenant's id (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md; threat T1 broken object-level authorization).
///
/// This is the aggregate + persistence story. There are NO HTTP endpoints (the reveal route
/// <c>POST /api/v1/sessions/{sessionId}/reveal</c> in csv/api_routes.csv is the later CORE-VIS-004
/// command), no <c>CanViewResource</c> policy (CORE-VIS-002), no preview-as-participant
/// (CORE-VIS-003) and no reveal command/idempotency or selected-participant reveal (CORE-VIS-004/005)
/// here; those are later stories and are deliberately not built. This contract takes explicit ids;
/// resolving the "current" organization or workspace from a request is the tenant context resolver
/// and later stories.
/// </summary>
public interface IVisibilityRuleRepository
{
    /// <summary>
    /// Finds the visibility rule with exactly the given id WITHIN the given organization and
    /// workspace, or <see langword="null"/> when no such rule exists there. The organization and
    /// workspace both scope the lookup, so a rule that exists under another organization's or
    /// workspace's id is never returned, even when the surrogate id matches (threat T5/T1).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or rule id is empty.
    /// </exception>
    Task<VisibilityRule?> FindByIdAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every visibility rule of the given workspace (owned by the given organization) in a
    /// deterministic order — sorted by the surrogate id, which is time-ordered (UUIDv7), so the
    /// sequence is stable and repeatable. The list is tenant- AND workspace-scoped: the predicate
    /// leads with <c>organization_id</c> and then matches <c>workspace_id</c>, so a foreign tenant's
    /// or a foreign workspace's rules are NEVER returned even when their ids would otherwise be
    /// addressable (threat T5/T1). This is NOT a list-everything method.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    Task<IReadOnlyList<VisibilityRule>> ListByWorkspaceAsync(
        Guid organizationId,
        Guid workspaceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the visibility rule with exactly the given id WITHIN the given organization, workspace AND
    /// SESSION, or <see langword="null"/> when no such rule exists there (CORE-SVIS-005). All three
    /// boundaries scope the lookup, so a rule that exists under another organization's, workspace's or
    /// session's id is never returned, even when the surrogate id matches (threat T5/T1). This is the
    /// session-scoped by-id read the visibility-rule read endpoint
    /// (<c>GET /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}</c>) performs: a reveal is
    /// session-scoped (CORE-SVIS-001), so a rule of another concurrent session of the same workspace is
    /// never reachable through this session's route (the cross-session leak; threat T5/T3). The predicate
    /// leads with <c>organization_id</c> then matches <c>workspace_id</c> and <c>session_id</c>, so the
    /// organization boundary is checked before the workspace boundary before the session.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id or rule id is empty.
    /// </exception>
    Task<VisibilityRule?> FindByIdInSessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        Guid id,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists ONE BOUNDED PAGE of the given session's visibility rules (of the given workspace, owned by the
    /// given organization) in the deterministic (time-ordered surrogate id) order, limited to
    /// <paramref name="take"/> rows starting at <paramref name="skip"/>, backing the bounded visibility-rule
    /// list route (<c>GET /api/v1/sessions/{sessionId}/visibility-rules</c>, CORE-SVIS-005 / CORE-DX-003).
    /// The page is exactly tenant-, workspace- AND SESSION-scoped (the predicate leads with
    /// <c>organization_id</c> then matches <c>workspace_id</c> and <c>session_id</c>), so a foreign tenant's,
    /// workspace's or session's rules are NEVER returned even when their ids would otherwise be addressable
    /// (threat T5/T1; the cross-session leak T3) — a reveal in a concurrent session of the same workspace is
    /// never enumerated here. The caller over-fetches one extra row (<c>take = limit + 1</c>) to decide
    /// <c>hasMore</c> without a second COUNT. Bounding the read means a single list response can never
    /// materialize the whole table (threat T9). It returns BOTH the audience-wide rule and every
    /// selected-participant rule of the session (the dimension is part of each returned rule).
    /// </summary>
    /// <exception cref="ArgumentException">The organization id, workspace id or session id is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="skip"/> is negative or <paramref name="take"/> is below one.
    /// </exception>
    Task<IReadOnlyList<VisibilityRule>> ListPageBySessionAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every visibility rule governing the given resource (named by type + id) WITHIN the given
    /// organization, workspace AND SESSION, in deterministic (time-ordered surrogate id) order. This is
    /// the session-scoped lookup the documented critical index
    /// <c>visibility_rules(session_id, resource_type, resource_id)</c> backs (docs/10_DATABASE_SCHEMA.md,
    /// CORE-SVIS-001) and the single one the session-scoped <see cref="VisibilityPolicy"/> and the reveal
    /// command perform. A reveal is session-scoped, so a rule in another session of the SAME workspace is
    /// NEVER returned — that is what stops a reveal in one session leaking into a concurrent session
    /// (threat T5/T3). The list is tenant-, workspace-, session- AND resource-scoped: the predicate leads
    /// with <c>organization_id</c>, then matches <c>workspace_id</c>, <c>session_id</c>,
    /// <c>resource_type</c> and <c>resource_id</c>, so a foreign tenant's, workspace's or session's rules
    /// are NEVER returned even when their ids would otherwise be addressable (threat T5/T1). It may return
    /// more than one rule for a resource (the index is non-unique). The resource id is matched as a plain
    /// column value (it is not a foreign key), so this method does not validate that the resource exists
    /// or is in the workspace — that coupling is the create-rule application flow's responsibility.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, session id or resource id is empty.
    /// </exception>
    Task<IReadOnlyList<VisibilityRule>> ListByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every visibility rule governing ANY of the given resources WITHIN the given organization,
    /// workspace AND SESSION, in deterministic (time-ordered surrogate id) order — the BATCH form of
    /// <see cref="ListByResourceAsync"/> (CORE-PERF-002). It reads the rules for a whole set of resources in
    /// ONE query (<c>WHERE organization_id = .. AND workspace_id = .. AND session_id = .. AND resource_id IN
    /// (..)</c>, on the session-scoped critical index), so a reconnect replay can resolve the audience
    /// visibility of every distinct subject in the replayed slice with a SINGLE lookup instead of one per
    /// event — replay cost does not grow with the number of events (it reuses the CORE-PERF-001 in-memory
    /// audience gate, just fed from one batched read). The filter is on the resource IDs (globally-unique
    /// surrogates), so the caller pairs each returned rule back to its requested (type, id) subject by the
    /// rule's own <see cref="VisibilityRule.ResourceType"/>/<see cref="VisibilityRule.ResourceId"/> (a rule
    /// for a different type that happened to share an id is simply not matched to a requested subject). Like
    /// the single-resource lookup it is tenant-, workspace- AND session-scoped (the predicate leads with
    /// <c>organization_id</c>, then <c>workspace_id</c>, then <c>session_id</c>), so a foreign tenant's,
    /// workspace's or session's rules are NEVER returned (threat T5/T1; the cross-session leak T3) and a
    /// reveal in a concurrent session never contributes. An empty resource set returns an empty list.
    /// </summary>
    /// <exception cref="ArgumentException">The organization id, workspace id or session id is empty.</exception>
    Task<IReadOnlyList<VisibilityRule>> ListByResourcesAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid sessionId,
        IReadOnlyCollection<Guid> resourceIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new visibility rule. Returns <see cref="VisibilityRuleAddResult.Added"/> on success, or
    /// <see cref="VisibilityRuleAddResult.Duplicate"/> when a rule already exists for the same (session,
    /// resource, DIMENSION) and the filtered unique index (CORE-SVIS-002) rejected the insert — typically
    /// a concurrent first-reveal that lost the create race, which the caller converges onto the existing
    /// rule rather than creating a second one. Foreign-key violations (a non-existent session, workspace
    /// or tenant) still surface as a <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>.
    /// </summary>
    Task<VisibilityRuleAddResult> AddAsync(VisibilityRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to a rule previously loaded through this repository — in particular a
    /// visibility-state change. The organization, workspace, id and governed resource of a rule are
    /// immutable (<see cref="VisibilityRule"/>), so an update only ever changes the visibility state
    /// and the update timestamp; it can never move the rule to another tenant, workspace or resource
    /// (threat T5). The caller is responsible for having loaded the rule through a tenant-scoped
    /// lookup.
    /// </summary>
    Task UpdateAsync(VisibilityRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// HARD-DELETES every visibility rule governing the given resource (named by type + id) WITHIN the
    /// given organization and workspace — both the audience-wide rule and every selected-participant
    /// rule for it — and returns the number removed (CORE-LIFE-003, the "Resource Lifecycle and
    /// Deletion" epic). This is the cleanup a resource's deletion requires: <c>resource_id</c> is a
    /// POLYMORPHIC reference (no database foreign key — a single column cannot foreign-key into
    /// scenes/content_blocks/entities), so the database can NOT cascade a rule when its governed resource
    /// is deleted; leaving the rule behind would be a DANGLING rule that a later resource minted with the
    /// same id could silently inherit (a visibility leak; threats T2/T5). So the application removes the
    /// rules explicitly when the resource is deleted (here, the <see cref="LiveCore.Api.Entities.EntityDeletionService"/>
    /// for an Entity), documented in docs/adr/0012-resource-deletion-cascades-dependents.md. The deletion
    /// is tenant-, workspace- AND resource-scoped (the predicate is the documented critical index shape
    /// <c>visibility_rules(workspace_id, resource_type, resource_id)</c> led by <c>organization_id</c>),
    /// so a foreign tenant's or workspace's rules are NEVER removed even when the resource id would
    /// otherwise be addressable (threat T5/T1). Removing the rules of a resource that has none is a no-op
    /// that returns 0.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or resource id is empty.
    /// </exception>
    Task<int> RemoveByResourceAsync(
        Guid organizationId,
        Guid workspaceId,
        VisibilityResourceType resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);
}
