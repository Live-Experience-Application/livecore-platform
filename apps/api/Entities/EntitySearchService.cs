using LiveCore.Api.Organizations;

namespace LiveCore.Api.Entities;

/// <summary>
/// Entity search within a workspace WITH VISIBILITY FILTERING (CORE-ENT-005, the last story of the
/// "Entity System and Templates" epic). Given a tenant, a workspace, the caller's WORKSPACE role and
/// generic <see cref="EntitySearchCriteria"/>, it returns the entities that role may see —
/// host-capable roles get every matching entity (the host-only-content view), audience roles get the
/// fail-closed visibility-filtered view. It is a plain, unit-testable application service over
/// <see cref="IEntityRepository"/> taking explicit inputs, exactly like
/// <see cref="Sessions.SessionParticipantJoinService"/> and <c>TenantContextResolver</c>; resolving
/// the "current" organization/workspace and role from a request is the tenant context resolver and a
/// later endpoint story (csv/api_routes.csv defines no entity route, so this story adds NO HTTP
/// endpoint).
///
/// THE VISIBILITY BOUNDARY (the genuine scope challenge of this story). The headline is "search ...
/// with visibility filtering", but the central Visibility module — visibility rules, audience
/// calculations, preview-as-participant, <c>CanViewResource</c>,
/// <c>GetVisibleResourcesForParticipant</c> — is the later CORE-VIS-* epic and does not exist yet,
/// and the architecture forbids computing entity visibility "ad hoc in many places"
/// (docs/02_ARCHITECTURE.md) and duplicating "visibility logic elsewhere"
/// (docs/05_MODULE_CONTRACTS.md). So this service does NOT build a parallel visibility engine. It
/// makes only the coarse, ROLE-level host-vs-audience split (<see cref="EntitySearchVisibility"/>,
/// the "View host-only content" row of docs/06_AUTHORIZATION_MATRIX.md):
/// <list type="bullet">
///   <item>HOST-CAPABLE roles (Owner/Admin/Host/CoHost) get the host-only-content view: every
///   entity in the workspace that matches the criteria.</item>
///   <item>AUDIENCE roles (Participant/Observer, the audit role Auditor, and any undefined role)
///   get the audience view, which is fail-closed EMPTY — the audience-visible subset is what the
///   future Visibility engine will compute server-side ("Participant visibility is computed
///   server-side", docs/06); until then nothing is visible. This mirrors the CORE-SES-005
///   participant-visible feed skeleton: real fail-closed authorization, empty content pending the
///   Visibility engine.</item>
/// </list>
/// The audience path is short-circuited BEFORE any database query, so an audience caller can never
/// learn whether any entity exists (no existence leak; threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// Tenant + workspace isolation. The host path reads through the existing tenant- AND
/// workspace-scoped <see cref="IEntityRepository"/> lookups (whose predicates lead with
/// <c>organization_id</c> then <c>workspace_id</c> — the organization boundary checked before the
/// workspace boundary, docs/06), so a search in one workspace never returns another workspace's or
/// another tenant's entities even when ids would otherwise be addressable (threat T5). There is no
/// list-everything path. The optional type filter is applied as a scoped lookup
/// (<see cref="IEntityRepository.ListByTypeAsync"/>); the optional name filter is applied as a
/// deterministic, collation-independent ORDINAL case-insensitive substring test
/// (<see cref="EntitySearchCriteria.MatchesName"/>) over the already-scoped candidate set, so the
/// search result does not depend on the database's collation.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): the search predicate is fully generic. The
/// name term is opaque data matched as a substring and the type filter is a surrogate id; there is
/// no branching on any type name and no vertical vocabulary in this source (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
internal sealed class EntitySearchService
{
    private readonly IEntityRepository _entities;

    public EntitySearchService(IEntityRepository entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entities = entities;
    }

    /// <summary>
    /// Searches the given workspace's entities for the given workspace role, applying the criteria
    /// and the host-vs-audience visibility split.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace to search within.</param>
    /// <param name="viewerRole">The caller's role in <paramref name="workspaceId"/>.</param>
    /// <param name="criteria">Normalized, validated search criteria (see <see cref="EntitySearchCriteria"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The host-only-content view (every matching entity) for a host-capable role, or the
    /// fail-closed empty audience view for any other role.
    /// </returns>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentNullException">The criteria is <see langword="null"/>.</exception>
    public async Task<EntitySearchResult> SearchAsync(
        Guid organizationId,
        Guid workspaceId,
        MembershipRole viewerRole,
        EntitySearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        // Empty ids can never address a stored workspace's entities, so the search fails fast
        // instead of returning an arbitrary set of rows (mirrors the repository's empty-id guards).
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(criteria);

        // Visibility split, FAIL-CLOSED and BEFORE any query. A non-host role gets the audience
        // view, which is empty until the Visibility engine (CORE-VIS) computes the audience-visible
        // subset. Short-circuiting here means the database is never queried for an audience caller,
        // so no entity existence can leak to a role that may not see host-only content (threats
        // T1/T5; docs/06 "View host-only content"). This is the role-level decision only — no
        // per-entity visibility rule is evaluated here (that is CORE-VIS, not duplicated).
        if (!EntitySearchVisibility.ViewsHostOnlyContent(viewerRole))
        {
            return EntitySearchResult.AudienceEmpty();
        }

        // Host-only-content view. Read through the tenant- and workspace-scoped repository lookups:
        // the optional type filter narrows to a single entity type (still workspace-scoped), else
        // the whole workspace. Both lead with the organization id then the workspace id, so another
        // tenant's or workspace's entities are never returned (threat T5), and both return a
        // deterministic (time-ordered surrogate id) order.
        var candidates = criteria.EntityTypeId is { } entityTypeId
            ? await _entities
                .ListByTypeAsync(organizationId, workspaceId, entityTypeId, cancellationToken)
                .ConfigureAwait(false)
            : await _entities
                .ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken)
                .ConfigureAwait(false);

        // Apply the optional name filter in memory over the already-scoped, already-ordered set.
        // The match is ORDINAL and case-insensitive, so it is deterministic and independent of the
        // database collation; the ordering from the repository is preserved. With no name filter
        // the scoped set is returned unchanged.
        if (!criteria.HasNameFilter)
        {
            return EntitySearchResult.Host(candidates);
        }

        var matched = candidates
            .Where(entity => criteria.MatchesName(entity.Name))
            .ToArray();

        return EntitySearchResult.Host(matched);
    }
}
