// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
    /// The AUDIENCE view: the visibility-filtered set returned to an audience caller. For an audience
    /// PARTICIPANT it holds exactly the entities the central Visibility engine
    /// (<see cref="Visibility.VisibilityPolicy.CanParticipantViewResourceAsync(System.Guid, System.Guid, System.Guid, System.Guid, Visibility.VisibilityResourceType, System.Guid, System.Threading.CancellationToken)"/>, CORE-VIS-005 + CORE-SVIS-004)
    /// reveals to that participant IN THEIR SESSION — an audience-wide visible rule of that session, or a
    /// visible rule of that session scoped to exactly them (CORE-API-006); a caller with no content-view
    /// standing (the audit role, any undefined role, an audience role with no participant or no session)
    /// receives the same view, fail-closed EMPTY, so the two are indistinguishable. No per-entity
    /// visibility logic is duplicated here (docs/02_ARCHITECTURE.md, docs/05_MODULE_CONTRACTS.md).
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
    /// Builds the AUDIENCE result over the entities the central Visibility engine revealed to the
    /// calling participant (CORE-API-006) — the visibility-filtered subset of the matching workspace
    /// entities. The set is already tenant- and workspace-scoped and contains only entities the
    /// participant may see (an audience-wide visible rule, or a visible rule scoped to exactly them);
    /// it is empty when nothing is revealed to them.
    /// </summary>
    public static EntitySearchResult Audience(IReadOnlyList<Entity> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new EntitySearchResult(EntitySearchView.AudienceVisibilityFiltered, items);
    }

    /// <summary>
    /// The fail-closed AUDIENCE result: an EMPTY visibility-filtered view returned to a caller with no
    /// content-view standing — the audit role, any undefined role, or an audience role with no
    /// identified participant. The query is not even run for this view, so no entity existence can leak
    /// to such a caller (threats T1/T5). It is indistinguishable from an audience participant who
    /// currently has nothing revealed to them.
    /// </summary>
    public static EntitySearchResult AudienceEmpty()
        => new(EntitySearchView.AudienceVisibilityFiltered, []);
}
