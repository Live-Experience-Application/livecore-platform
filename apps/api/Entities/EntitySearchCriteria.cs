// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Generic, product-neutral search criteria for entity search within a workspace (CORE-ENT-005).
/// It is the data a caller supplies to <see cref="EntitySearchService.SearchAsync"/> to narrow a
/// workspace's entities: an optional case-insensitive name substring and an optional entity-type
/// filter. Both are OPTIONAL and combine with AND; <see cref="MatchAll"/> is the no-filter criteria
/// that matches every entity in the (already tenant- and workspace-scoped) candidate set.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): the criteria are entirely generic. The
/// name term is opaque caller-supplied DATA matched as a plain substring — Core never inspects its
/// vocabulary, never branches on a type name and never hardcodes any vertical term (there is no
/// <c>if name == "..."</c>). The type filter is a surrogate <c>entity_type_id</c>, not a type name.
/// So a vertical may search for its own runtime vocabulary, but this source contains none.
///
/// Normalization and validation happen once, in <see cref="Create"/>, so the rest of the search
/// pipeline can treat the criteria as already-canonical: the name term is trimmed and a blank term
/// becomes "no name filter" (<see langword="null"/>), an over-long term is rejected (a substring
/// longer than the longest possible name can never match and is treated as hostile/broken input,
/// mirroring the aggregate's reject-oversized discipline), and an explicitly empty type id is
/// rejected (an empty id can never address a stored type; <see langword="null"/> means "no type
/// filter"). Name matching is ORDINAL and case-insensitive (<see cref="MatchesName"/>), so it is
/// deterministic and collation-independent — it never depends on the database's collation and gives
/// the same result on SQLite and PostgreSQL.
/// </summary>
public sealed class EntitySearchCriteria
{
    /// <summary>
    /// Maximum accepted length of the name substring term. A term longer than the longest possible
    /// entity name (<see cref="Entity.MaxNameLength"/>) can never be a substring of any name, so it
    /// is rejected as hostile or broken input rather than silently matching nothing.
    /// </summary>
    public const int MaxNameContainsLength = Entity.MaxNameLength;

    private EntitySearchCriteria(string? nameContains, Guid? entityTypeId)
    {
        NameContains = nameContains;
        EntityTypeId = entityTypeId;
    }

    /// <summary>
    /// The case-insensitive name substring to match, already trimmed; <see langword="null"/> means
    /// "no name filter" (a blank term is normalized to <see langword="null"/> in
    /// <see cref="Create"/>). Never an empty or whitespace-only string.
    /// </summary>
    public string? NameContains { get; }

    /// <summary>
    /// The entity type to restrict the search to (its surrogate <c>entity_type_id</c>);
    /// <see langword="null"/> means "all types". Never <see cref="Guid.Empty"/> (rejected in
    /// <see cref="Create"/>).
    /// </summary>
    public Guid? EntityTypeId { get; }

    /// <summary>
    /// Whether a name substring filter is present. When <see langword="false"/> the search matches
    /// on the type filter (if any) alone.
    /// </summary>
    public bool HasNameFilter => NameContains is not null;

    /// <summary>The no-filter criteria: matches every entity in the scoped candidate set.</summary>
    public static EntitySearchCriteria MatchAll { get; } = new(null, null);

    /// <summary>
    /// Builds normalized, validated criteria. The name term is trimmed and a blank term becomes "no
    /// name filter"; the type id, when supplied, must be non-empty.
    /// </summary>
    /// <param name="nameContains">
    /// Optional name substring to match (case-insensitively); <see langword="null"/>, empty or
    /// whitespace means "no name filter".
    /// </param>
    /// <param name="entityTypeId">
    /// Optional entity type to restrict to; <see langword="null"/> means "all types".
    /// </param>
    /// <exception cref="ArgumentException">
    /// The trimmed name term is longer than <see cref="MaxNameContainsLength"/>, or
    /// <paramref name="entityTypeId"/> was supplied as <see cref="Guid.Empty"/> (an empty id can
    /// never address a stored type).
    /// </exception>
    public static EntitySearchCriteria Create(string? nameContains = null, Guid? entityTypeId = null)
    {
        var trimmed = nameContains?.Trim();
        var normalizedName = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (normalizedName is not null && normalizedName.Length > MaxNameContainsLength)
        {
            throw new ArgumentException(
                $"The name search term must be at most {MaxNameContainsLength} characters.",
                nameof(nameContains));
        }

        if (entityTypeId == Guid.Empty)
        {
            throw new ArgumentException(
                "The entity type filter must not be empty; pass null for no type filter.",
                nameof(entityTypeId));
        }

        return new EntitySearchCriteria(normalizedName, entityTypeId);
    }

    /// <summary>
    /// Whether the given entity name satisfies the name filter. With no name filter every name
    /// matches; otherwise the match is an ORDINAL, case-insensitive substring test, so it is
    /// deterministic and independent of the database collation (the same result on SQLite and
    /// PostgreSQL). The name is treated purely as opaque data — no vocabulary is inspected (the
    /// template boundary, docs/04_PRODUCT_BOUNDARIES.md).
    /// </summary>
    public bool MatchesName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return NameContains is null
            || name.Contains(NameContains, StringComparison.OrdinalIgnoreCase);
    }
}
