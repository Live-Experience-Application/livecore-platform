namespace LiveCore.Api.Entities;

/// <summary>
/// Which VIEW of a workspace's entities a search returned (CORE-ENT-005) — the outcome of the
/// host-vs-audience visibility split (<see cref="EntitySearchVisibility"/>). It is recorded on
/// <see cref="EntitySearchResult"/> so callers and tests can assert which path ran (in particular
/// that an audience caller received the fail-closed empty view rather than the host-only-content
/// view), never as an authorization input.
/// </summary>
public enum EntitySearchView
{
    /// <summary>
    /// The HOST-ONLY-CONTENT view: every entity in the workspace matching the criteria. Returned to
    /// the roles the authorization matrix grants "View host-only content" = <c>yes</c>
    /// (Owner/Admin/Host/CoHost; docs/06_AUTHORIZATION_MATRIX.md).
    /// </summary>
    HostOnlyContent = 1,

    /// <summary>
    /// The AUDIENCE view: the visibility-filtered set an audience caller (Participant/Observer/
    /// Auditor and any non-host role) may see. The actual filtering is computed server-side by the
    /// Visibility engine (CORE-VIS), which does not exist yet, so this view is legitimately EMPTY
    /// for now (fail-closed) — mirroring the CORE-SES-005 participant-visible feed skeleton. No
    /// per-entity visibility logic is duplicated here (docs/02_ARCHITECTURE.md,
    /// docs/05_MODULE_CONTRACTS.md).
    /// </summary>
    AudienceVisibilityFiltered = 2,
}

/// <summary>
/// The result of an entity search within a workspace (CORE-ENT-005): the matching entities together
/// with the <see cref="EntitySearchView"/> that produced them. The set is already tenant- and
/// workspace-scoped (it comes from the scoped <see cref="IEntityRepository"/> lookups) and, for the
/// audience view, fail-closed empty.
///
/// The result carries the <see cref="Entity"/> aggregates themselves (whose <c>ToString</c> is
/// log-safe and hides the name/attribute content; threat T7 in docs/07_SECURITY_THREAT_MODEL.md);
/// projecting them into a host or participant response DTO is an endpoint concern and there is no
/// entity HTTP route (csv/api_routes.csv), so no DTO is invented here.
/// </summary>
public sealed class EntitySearchResult
{
    private EntitySearchResult(EntitySearchView view, IReadOnlyList<Entity> items)
    {
        View = view;
        Items = items;
    }

    /// <summary>Which view produced <see cref="Items"/> (host-only-content vs audience).</summary>
    public EntitySearchView View { get; }

    /// <summary>
    /// The matching entities in deterministic (time-ordered surrogate id) order, as returned by the
    /// scoped repository lookups and narrowed by the criteria. Empty for the audience view.
    /// </summary>
    public IReadOnlyList<Entity> Items { get; }

    /// <summary>Whether this is the host-only-content view (every matching workspace entity).</summary>
    public bool IsHostView => View == EntitySearchView.HostOnlyContent;

    /// <summary>
    /// Builds the HOST-ONLY-CONTENT result over the given matching entities (every matching
    /// workspace entity).
    /// </summary>
    public static EntitySearchResult Host(IReadOnlyList<Entity> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new EntitySearchResult(EntitySearchView.HostOnlyContent, items);
    }

    /// <summary>
    /// The AUDIENCE result: an EMPTY, fail-closed visibility-filtered view. Returned for every
    /// non-host role until the Visibility engine (CORE-VIS) computes the audience-visible subset.
    /// The query is not even run for this view, so no entity existence can leak to an audience
    /// caller (threats T1/T5).
    /// </summary>
    public static EntitySearchResult AudienceEmpty()
        => new(EntitySearchView.AudienceVisibilityFiltered, []);
}
