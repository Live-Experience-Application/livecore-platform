// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Plan DEFINITION aggregate of the Entitlements module (CORE-ENTL-001, the plan and entitlement definition
/// model story of the "Entitlements and Quotas" epic).
///
/// A <see cref="PlanDefinition"/> is the Core-owned, product-neutral catalog entry for ONE generic plan — a
/// named bundle of granted entitlements (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md core concept
/// <c>PlanDefinition</c>). It records which generic <see cref="EntitlementDefinition"/>s the plan grants and at
/// what value (its <see cref="Entitlements"/>), so the plan is a meaningful, server-side bundle rather than a
/// bare name. A later story (CORE-ENTL-002) assigns a subject the entitlements of a plan; this story models
/// the plan and its grants.
///
/// GENERIC — THE EPIC ACCEPTANCE CRITERION ("Generic entitlements can be defined without vertical
/// terminology"). The plan is identified by a generic, dotted, lower-case <see cref="Key"/> (such as
/// <c>free</c> or <c>standard</c>), and carries no vertical product language anywhere (AGENTS.md,
/// csv/forbidden_core_terms.csv). The specific commercial plans of a vertical (and their concrete values,
/// which docs/21 lists only as "Recommended ... mapping" "examples ... to be finalized") are vertical SEED
/// DATA supplied by the vertical (docs/04_PRODUCT_BOUNDARIES.md "Allowed vertical extension mechanisms" -
/// "seed data"), never hardcoded in Core source; Core provides only the generic model.
///
/// VALUE SHAPE IS ENFORCED. A grant's value shape is fixed by the referenced
/// <see cref="EntitlementDefinition.ValueKind"/>: <see cref="GrantFlag"/> only binds a boolean to a flag
/// entitlement and <see cref="GrantQuota"/> only binds a numeric limit (or unlimited) to a quota entitlement,
/// so a plan can never bind the wrong value shape, and an entitlement is granted at most once per plan. The
/// value is decided server-side, never trusted from a client (docs/21 "Never trust client-side premium
/// flags").
///
/// GLOBAL CATALOG, NOT TENANT CONTENT. Like <see cref="EntitlementDefinition"/>, the plan is the
/// deployment-wide monetization catalog (mirrors the global <c>organizations</c>/<c>users</c>/<c>templates</c>
/// tables), so it carries NO <c>organization_id</c> and is not tenant-scoped, and there is no role-based or
/// tenant-scoped projection in this story.
///
/// SOFT LIFECYCLE, NOT HARD DELETE. A plan is business data later subject assignments reference, so it is
/// retired with <see cref="Deactivate"/> rather than hard-deleted (docs/10_DATABASE_SCHEMA.md). <see cref="Key"/>
/// is immutable, so a plan key can never silently change meaning.
/// </summary>
public sealed class PlanDefinition
{
    /// <summary>Maximum stored length of the generic <see cref="Key"/>; keeps the unique index key bounded.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Maximum accepted display-name length. Informational metadata; longer values are rejected.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>Maximum accepted description length. Optional informational metadata; longer values are rejected.</summary>
    public const int MaxDescriptionLength = 1024;

    // Canonical key shape: lower-case alphanumeric segments separated by single dots (mirrors the entitlement
    // key shape) — stable, predictable and free of casing ambiguity or control characters.
    private static readonly Regex _keyPattern = new(
        "^[a-z0-9]+(?:\\.[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<PlanEntitlement> _entitlements = [];

    private PlanDefinition(
        Guid id,
        string key,
        string displayName,
        string? description,
        bool isActive,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Plan definition id must not be empty.", nameof(id));
        }

        if (!IsValidKey(key))
        {
            throw new ArgumentException("Key violates the plan key invariants.", nameof(key));
        }

        if (!IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name violates the display-name invariants.", nameof(displayName));
        }

        if (!IsValidDescription(description))
        {
            throw new ArgumentException("Description violates the description invariants.", nameof(description));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A plan definition cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        Key = key;
        DisplayName = displayName;
        Description = description;
        IsActive = isActive;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private PlanDefinition()
    {
        Key = null!;
        DisplayName = null!;
    }

    /// <summary>
    /// Surrogate key of the plan-definition row (UUID version 7, time-ordered per docs/10_DATABASE_SCHEMA.md).
    /// The stable natural key callers look up by is <see cref="Key"/>.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Stable, lower-case, dotted, globally unique generic KEY of the plan (such as <c>free</c> or
    /// <c>standard</c>). Immutable; comparisons are exact (ordinal).
    /// </summary>
    public string Key { get; }

    /// <summary>Human-readable, generic display name of the plan (informational metadata only).</summary>
    public string DisplayName { get; private set; }

    /// <summary>Optional generic description of the plan, or <see langword="null"/>. Informational only.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Whether the plan is currently offered. A retired (<see cref="Deactivate"/>d) plan stays in the catalog
    /// so existing assignments resolve, but is no longer offered (soft delete; docs/10_DATABASE_SCHEMA.md).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When this plan was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this plan was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// The entitlements this plan grants, each binding an <see cref="EntitlementDefinition"/> to a concrete
    /// value (read-only). An entitlement appears at most once.
    /// </summary>
    public IReadOnlyList<PlanEntitlement> Entitlements => _entitlements;

    /// <summary>
    /// Defines a new, active plan with the given generic key and display metadata. The plan starts with no
    /// grants; add them with <see cref="GrantFlag"/> / <see cref="GrantQuota"/>. The key is canonicalized
    /// (trimmed and lower-cased) once here so the persisted value and every later lookup stay consistent.
    /// </summary>
    /// <param name="key">The generic, dotted, lower-case plan key (required, within <see cref="MaxKeyLength"/>).</param>
    /// <param name="displayName">The generic display name (non-blank, within <see cref="MaxDisplayNameLength"/>).</param>
    /// <param name="description">An optional generic description (within <see cref="MaxDescriptionLength"/>); null or blank stores null.</param>
    /// <param name="createdAt">When the plan is created.</param>
    /// <exception cref="ArgumentException">The key, display name or description violates an invariant.</exception>
    public static PlanDefinition Define(
        string key,
        string displayName,
        string? description,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            CanonicalizeKey(key),
            displayName?.Trim() ?? string.Empty,
            NormalizeDescription(description),
            isActive: true,
            createdAt,
            createdAt);

    /// <summary>
    /// Grants a boolean FLAG entitlement to this plan. The definition must be an active
    /// <see cref="EntitlementValueKind.Flag"/> entitlement, and the plan must not already grant it. The
    /// granted value is recorded server-side (docs/21 "Never trust client-side premium flags").
    /// </summary>
    /// <param name="definition">The active flag entitlement definition to grant.</param>
    /// <param name="value">The boolean value the plan grants for it.</param>
    /// <returns>The created grant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The definition is not a flag, or is inactive.</exception>
    /// <exception cref="InvalidOperationException">The plan already grants the entitlement.</exception>
    public PlanEntitlement GrantFlag(EntitlementDefinition definition, bool value)
    {
        var entitlementId = ValidateGrantable(definition, EntitlementValueKind.Flag);
        var grant = PlanEntitlement.CreateFlag(Id, entitlementId, value);
        _entitlements.Add(grant);
        return grant;
    }

    /// <summary>
    /// Grants a numeric QUOTA entitlement to this plan. The definition must be an active
    /// <see cref="EntitlementValueKind.Quota"/> entitlement, and the plan must not already grant it. A
    /// <see langword="null"/> limit is an UNLIMITED (fair-use) grant; a non-null limit must be non-negative.
    /// </summary>
    /// <param name="definition">The active quota entitlement definition to grant.</param>
    /// <param name="limit">The numeric cap, or null for an unlimited (fair-use) grant.</param>
    /// <returns>The created grant.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentException">The definition is not a quota, or is inactive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
    /// <exception cref="InvalidOperationException">The plan already grants the entitlement.</exception>
    public PlanEntitlement GrantQuota(EntitlementDefinition definition, long? limit)
    {
        var entitlementId = ValidateGrantable(definition, EntitlementValueKind.Quota);
        if (limit is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "A quota limit must not be negative; pass null for an unlimited (fair-use) grant.");
        }

        var grant = PlanEntitlement.CreateQuota(Id, entitlementId, limit);
        _entitlements.Add(grant);
        return grant;
    }

    /// <summary>Whether this plan grants the entitlement with the given definition id. Empty ids match nothing.</summary>
    public bool GrantsEntitlement(Guid entitlementDefinitionId)
        => entitlementDefinitionId != Guid.Empty
            && _entitlements.Any(grant => grant.EntitlementDefinitionId == entitlementDefinitionId);

    /// <summary>
    /// Updates the generic display metadata. The key is immutable, so this never changes the plan's identity.
    /// </summary>
    /// <exception cref="ArgumentException">The display name or description violates an invariant.</exception>
    public void UpdateDetails(string displayName, string? description, DateTimeOffset updatedAt)
    {
        var trimmedName = displayName?.Trim() ?? string.Empty;
        if (!IsValidDisplayName(trimmedName))
        {
            throw new ArgumentException("Display name violates the display-name invariants.", nameof(displayName));
        }

        var normalizedDescription = NormalizeDescription(description);

        DisplayName = trimmedName;
        Description = normalizedDescription;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Retires the plan (soft delete): it stays in the catalog so existing assignments resolve, but is no
    /// longer offered. Idempotent.
    /// </summary>
    public void Deactivate(DateTimeOffset updatedAt)
    {
        IsActive = false;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Brings a retired plan back into the offered catalog. Idempotent.</summary>
    public void Reactivate(DateTimeOffset updatedAt)
    {
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether this plan is addressed by exactly the given key. Matching is exact (ordinal); blank or null
    /// values match nothing.
    /// </summary>
    public bool HasKey(string? key)
        => !string.IsNullOrWhiteSpace(key) && string.Equals(Key, key, StringComparison.Ordinal);

    /// <summary>Whether this plan is the one identified by the given id. Empty ids match nothing.</summary>
    public bool HasId(Guid id) => id != Guid.Empty && Id == id;

    /// <summary>
    /// Whether the given value is a valid generic plan key: non-blank, within the length bound and a
    /// lower-case dotted alphanumeric shape.
    /// </summary>
    public static bool IsValidKey(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= MaxKeyLength
            && _keyPattern.IsMatch(value);

    /// <summary>Whether the given value is a valid display name: non-blank, bounded and free of control characters.</summary>
    public static bool IsValidDisplayName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxDisplayNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid description: either null/blank (no description) or non-blank, bounded
    /// and free of control characters.
    /// </summary>
    public static bool IsValidDescription(string? value)
        => string.IsNullOrWhiteSpace(value)
            || (value.Length <= MaxDescriptionLength && !value.Any(char.IsControl));

    /// <summary>
    /// Canonicalizes a key to its stored form (trimmed and lower-cased using the invariant culture) and
    /// validates it.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid key after canonicalization.</exception>
    public static string CanonicalizeKey(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidKey(canonical))
        {
            throw new ArgumentException("Key violates the plan key invariants.", nameof(value));
        }

        return canonical;
    }

    private Guid ValidateGrantable(EntitlementDefinition definition, EntitlementValueKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // The granted value shape is fixed by the entitlement definition: a plan can never bind the wrong
        // value shape to an entitlement (docs/21). The value is decided server-side.
        if (definition.ValueKind != expectedKind)
        {
            throw new ArgumentException(
                $"Entitlement '{definition.Key}' is a {definition.ValueKind}, not a {expectedKind}.",
                nameof(definition));
        }

        // A retired entitlement is no longer offered, so a plan may not grant it.
        if (!definition.IsActive)
        {
            throw new ArgumentException(
                $"Entitlement '{definition.Key}' is inactive and cannot be granted.",
                nameof(definition));
        }

        // A plan grants each entitlement at most once (also enforced by the unique
        // plan_entitlements(plan_definition_id, entitlement_definition_id) index).
        if (GrantsEntitlement(definition.Id))
        {
            throw new InvalidOperationException(
                $"The plan already grants entitlement '{definition.Key}'.");
        }

        return definition.Id;
    }

    private static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!IsValidDescription(trimmed))
        {
            throw new ArgumentException("Description violates the description invariants.", nameof(description));
        }

        return trimmed;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: plan id, key, active flag and grant
    /// count. The display name and description are kept out of logs (threat T7).
    /// </summary>
    public override string ToString()
        => $"PlanDefinition {Id} key={Key} active={IsActive} grants={_entitlements.Count}";
}
