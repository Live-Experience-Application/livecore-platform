using LiveCore.Api.Organizations;
using LiveCore.Api.Visibility;

namespace LiveCore.Api.Entities;

/// <summary>
/// Entity search within a workspace WITH VISIBILITY FILTERING (CORE-ENT-005, retrofitted by
/// CORE-API-006 to apply the REAL audience visibility filter). Given a tenant, a workspace, the
/// caller's WORKSPACE role, the optional calling PARTICIPANT and generic
/// <see cref="EntitySearchCriteria"/>, it returns the entities that caller may see —
/// host-capable roles get every matching entity (the host-only-content view), an audience participant
/// gets exactly the entities REVEALED to them, and every other role gets the fail-closed empty view.
/// It is a plain, unit-testable application service over <see cref="IEntityRepository"/> and the
/// central <see cref="VisibilityPolicy"/> taking explicit inputs, exactly like
/// <see cref="VisibilityPreviewService"/> and <c>TenantContextResolver</c>; resolving the "current"
/// organization/workspace, role and participant from a request is the tenant context resolver and a
/// later endpoint story (csv/api_routes.csv defines no entity route, so this story adds NO HTTP
/// endpoint).
///
/// THE VISIBILITY BOUNDARY (the genuine scope challenge of this story). The headline is "search ...
/// with visibility filtering". The role-level host-vs-audience split
/// (<see cref="EntitySearchVisibility"/>, the "View host-only content" row of
/// docs/06_AUTHORIZATION_MATRIX.md) decides WHICH path a caller takes; the audience path then defers
/// the actual per-entity decision to the central Visibility module so this service does NOT build a
/// parallel visibility engine (docs/02_ARCHITECTURE.md: "entity visibility is not computed ad hoc in
/// many places"; docs/05_MODULE_CONTRACTS.md: "Do not duplicate visibility logic elsewhere"):
/// <list type="bullet">
///   <item>HOST-CAPABLE roles (Owner/Admin/Host/CoHost) get the host-only-content view: every entity
///   in the workspace that matches the criteria, regardless of any visibility rule. The calling
///   participant is irrelevant on this path.</item>
///   <item>An AUDIENCE PARTICIPANT (a Participant/Observer role with an identified participant) gets
///   the entities the central Visibility engine says are visible TO THEM: each matching candidate is
///   routed through
///   <see cref="VisibilityPolicy.CanParticipantViewResourceAsync"/> over
///   <see cref="VisibilityResourceType.Entity"/> — the SAME participant-aware primitive
///   <see cref="VisibilityPreviewService"/> and the realtime recipient resolver use, so entity search
///   can never diverge from the participant-visible feed or per-resource access. An entity is kept iff
///   an AUDIENCE-WIDE visible rule, or a visible rule scoped to EXACTLY this participant (a
///   selected-participant private reveal), applies; an entity revealed only to a DIFFERENT participant
///   is excluded — the selected-participant guarantee, fail-closed (threats T1/T5 in
///   docs/07_SECURITY_THREAT_MODEL.md).</item>
///   <item>EVERY OTHER caller — the audit role Auditor (audit-only on the content rows, never a live
///   content grant), any undefined role, and an audience role with NO identified participant — gets
///   the fail-closed EMPTY view. This path is short-circuited BEFORE any database query, so a caller
///   with no content-view standing can never learn whether any entity exists (no existence leak;
///   threats T1/T5; docs/06 "View host-only content").</item>
/// </list>
///
/// Tenant + workspace isolation. Every path reads through the existing tenant- AND workspace-scoped
/// <see cref="IEntityRepository"/> lookups (whose predicates lead with <c>organization_id</c> then
/// <c>workspace_id</c> — the organization boundary checked before the workspace boundary, docs/06), so
/// a search in one workspace never returns another workspace's or another tenant's entities even when
/// ids would otherwise be addressable (threat T5), and the audience visibility lookup is itself tenant-
/// and workspace-scoped, so a rule in another tenant or workspace can never reveal an entity here.
/// There is no list-everything path. The optional type filter is applied as a scoped lookup
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
    private readonly VisibilityPolicy _policy;

    public EntitySearchService(IEntityRepository entities, VisibilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(policy);
        _entities = entities;
        _policy = policy;
    }

    /// <summary>
    /// Searches the given workspace's entities for the given workspace role (and, for an audience
    /// participant, the given participant), applying the criteria and the host-vs-audience visibility
    /// filter.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace to search within.</param>
    /// <param name="viewerRole">The caller's role in <paramref name="workspaceId"/>.</param>
    /// <param name="participantId">
    /// The calling participant when the caller is an audience participant viewing their own feed;
    /// <see langword="null"/> (or <see cref="Guid.Empty"/>) means "no identified participant". It is
    /// only consulted for an audience role: the host path sees every entity regardless, and a non-host
    /// non-audience role fails closed to the empty view whether or not a participant is supplied. The
    /// caller (a later endpoint) is responsible for passing a participant the caller legitimately owns
    /// (the participant-visible-feed authorization model of docs/06), exactly as
    /// <see cref="VisibilityPolicy"/> trusts the passed role.
    /// </param>
    /// <param name="criteria">Normalized, validated search criteria (see <see cref="EntitySearchCriteria"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The host-only-content view (every matching entity) for a host-capable role, the
    /// visibility-filtered set an audience participant may see, or the fail-closed empty view for any
    /// other caller.
    /// </returns>
    /// <exception cref="ArgumentException">The organization id or workspace id is empty.</exception>
    /// <exception cref="ArgumentNullException">The criteria is <see langword="null"/>.</exception>
    public async Task<EntitySearchResult> SearchAsync(
        Guid organizationId,
        Guid workspaceId,
        MembershipRole viewerRole,
        Guid? participantId,
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

        var viewsHostOnlyContent = EntitySearchVisibility.ViewsHostOnlyContent(viewerRole);

        // An audience participant is an audience role (Participant/Observer) WITH an identified
        // participant; only then can the per-participant visibility be computed. An empty/absent
        // participant id leaves the audience role with no participant to compute for, so it falls into
        // the fail-closed branch below rather than reaching the per-participant policy (which rejects an
        // empty id). The role classification delegates to the central Visibility module so the role sets
        // are never duplicated here (docs/05_MODULE_CONTRACTS.md).
        var audienceParticipant = !viewsHostOnlyContent
            && EntitySearchVisibility.ViewsAudienceFilteredContent(viewerRole)
            && participantId is { } candidateId
            && candidateId != Guid.Empty
                ? participantId
                : null;

        // FAIL-CLOSED, BEFORE any query. A caller that is neither a host-capable role nor an audience
        // participant — the audit role, any undefined role, and an audience role with no identified
        // participant — gets the empty view. Short-circuiting here means the database is never queried
        // for such a caller, so no entity existence can leak to a role that may not see host-only
        // content (threats T1/T5; docs/06 "View host-only content").
        if (!viewsHostOnlyContent && audienceParticipant is null)
        {
            return EntitySearchResult.AudienceEmpty();
        }

        // Candidate set, tenant- and workspace-scoped, shared by the host and audience-participant
        // paths. Read through the existing scoped repository lookups: the optional type filter narrows
        // to a single entity type (still workspace-scoped), else the whole workspace. Both lead with the
        // organization id then the workspace id, so another tenant's or workspace's entities are never
        // returned (threat T5), and both return a deterministic (time-ordered surrogate id) order.
        IReadOnlyList<Entity> candidates = criteria.EntityTypeId is { } entityTypeId
            ? await _entities
                .ListByTypeAsync(organizationId, workspaceId, entityTypeId, cancellationToken)
                .ConfigureAwait(false)
            : await _entities
                .ListByWorkspaceAsync(organizationId, workspaceId, cancellationToken)
                .ConfigureAwait(false);

        // Apply the optional name filter in memory over the already-scoped, already-ordered set. The
        // match is ORDINAL and case-insensitive, so it is deterministic and independent of the database
        // collation; the ordering from the repository is preserved. With no name filter the scoped set
        // is unchanged.
        if (criteria.HasNameFilter)
        {
            candidates = candidates
                .Where(entity => criteria.MatchesName(entity.Name))
                .ToArray();
        }

        // HOST-ONLY-CONTENT view: every matching entity, regardless of any visibility rule.
        if (viewsHostOnlyContent)
        {
            return EntitySearchResult.Host(candidates);
        }

        // AUDIENCE-PARTICIPANT view: keep only the entities this participant may actually see, decided
        // by the central per-participant visibility primitive (CORE-VIS-005) — the SAME one the
        // participant-visible feed (VisibilityPreviewService) and the realtime recipient resolver use,
        // so entity search can never diverge (docs/05: do not duplicate visibility logic elsewhere). An
        // entity is visible iff an audience-wide visible rule, or a visible rule scoped to EXACTLY this
        // participant, applies; one revealed only to another participant is excluded (fail-closed; the
        // selected-participant guarantee, threat T5). The lookup is tenant- and workspace-scoped, so a
        // rule in another tenant or workspace never reveals an entity here.
        var participant = audienceParticipant!.Value;
        var visible = new List<Entity>(candidates.Count);
        foreach (var entity in candidates)
        {
            var canView = await _policy
                .CanParticipantViewResourceAsync(
                    organizationId,
                    workspaceId,
                    participant,
                    VisibilityResourceType.Entity,
                    entity.Id,
                    cancellationToken)
                .ConfigureAwait(false);

            if (canView)
            {
                visible.Add(entity);
            }
        }

        return EntitySearchResult.Audience(visible);
    }
}
