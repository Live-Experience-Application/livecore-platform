using System.Text.RegularExpressions;

namespace LiveCore.Api.Entitlements;

/// <summary>
/// Entitlement DEFINITION aggregate of the Entitlements module (CORE-ENTL-001, the plan and entitlement
/// definition model story of the "Entitlements and Quotas" epic).
///
/// An <see cref="EntitlementDefinition"/> is the Core-owned, product-neutral catalog entry for ONE generic
/// entitlement — a server-enforced usage limit or premium capability (docs/05_MODULE_CONTRACTS.md gives the
/// Entitlements/Quotas surface to Core; docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Limits ... must be
/// enforced server-side. Otherwise users can bypass mobile UI restrictions"). It records WHAT entitlements
/// exist in the deployment and their value shape; a <see cref="PlanDefinition"/> then grants concrete values
/// for them, and a later story (CORE-ENTL-002) assigns them to a subject.
///
/// GENERIC — THE EPIC ACCEPTANCE CRITERION ("Generic entitlements can be defined without vertical
/// terminology"). The definition is identified by a generic, dotted, lower-case <see cref="Key"/> (such as
/// <c>workspace.active.max</c> or <c>ads.disabled</c> — docs/21 "Generic entitlement keys"), and carries no
/// vertical product language anywhere: the key, the display name and the description are generic Core
/// vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv). A vertical maps a key to its own paywall copy in
/// its UI (docs/21 "a vertical may display these as ..."), but Core stores only the generic definition, so the
/// model can be populated entirely without vertical terminology.
///
/// VALUE KIND. <see cref="ValueKind"/> classifies the entitlement as a boolean <see cref="EntitlementValueKind.Flag"/>
/// or a numeric <see cref="EntitlementValueKind.Quota"/> (csv/mobile_entitlement_catalog.csv <c>type</c>
/// column). It fixes the shape of the value a plan may grant, so a plan can never bind the wrong value shape
/// to the entitlement; the value is decided and enforced server-side, never trusted from a client (docs/21
/// "Never trust client-side premium flags").
///
/// GLOBAL CATALOG, NOT TENANT CONTENT. The definition is the deployment-wide monetization catalog (mirrors the
/// global <c>organizations</c>/<c>users</c>/<c>templates</c> tables), so it carries NO <c>organization_id</c>
/// and is not tenant-scoped: there is no per-tenant copy, no host-only body and no audience to project away —
/// the subject-facing view ("user-visible premium state comes only from server entitlements") is the later
/// subject-entitlement story (CORE-ENTL-002). Because nothing here is tenant-scoped content, there is no
/// role-based or tenant-scoped projection in this story (unlike the recap/export models).
///
/// SOFT LIFECYCLE, NOT HARD DELETE. A definition is business data that other rows (plan grants, later subject
/// entitlements) reference, so it is never hard-deleted; it is retired with <see cref="Deactivate"/>
/// (and can be brought back with <see cref="Reactivate"/>), keeping existing references resolvable
/// (docs/10_DATABASE_SCHEMA.md: "avoid hard-delete for business data; use soft delete where needed").
/// <see cref="Key"/> and <see cref="ValueKind"/> are immutable for the lifetime of the definition, so a key
/// can never silently change meaning or value shape.
/// </summary>
public sealed class EntitlementDefinition
{
    /// <summary>
    /// Maximum stored length of the generic <see cref="Key"/>. Generous enough for the documented dotted keys
    /// (docs/21) while keeping the unique index key bounded.
    /// </summary>
    public const int MaxKeyLength = 128;

    /// <summary>Maximum accepted display-name length. Informational metadata; longer values are rejected.</summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>Maximum accepted description length. Optional informational metadata; longer values are rejected.</summary>
    public const int MaxDescriptionLength = 1024;

    // Canonical key shape: lower-case alphanumeric segments separated by single dots (no leading, trailing or
    // doubled dot). This keeps the entitlement key stable, predictable and free of casing ambiguity or control
    // characters that could blur which capability is being granted, and matches the documented dotted generic
    // keys (docs/21 "Generic entitlement keys", e.g. workspace.active.max).
    private static readonly Regex _keyPattern = new(
        "^[a-z0-9]+(?:\\.[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private EntitlementDefinition(
        Guid id,
        string key,
        EntitlementValueKind valueKind,
        string displayName,
        string? description,
        bool isActive,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entitlement definition id must not be empty.", nameof(id));
        }

        if (!IsValidKey(key))
        {
            throw new ArgumentException("Key violates the entitlement key invariants.", nameof(key));
        }

        if (!Enum.IsDefined(valueKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueKind),
                valueKind,
                "Value kind is not a defined entitlement value kind.");
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
                "An entitlement definition cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        Key = key;
        ValueKind = valueKind;
        DisplayName = displayName;
        Description = description;
        IsActive = isActive;
        // Timestamps are normalized to UTC so the persisted timestamptz values are offset-independent
        // (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private EntitlementDefinition()
    {
        // Non-null reference defaults for the EF materialization path; real values are set from the stored
        // columns.
        Key = null!;
        DisplayName = null!;
    }

    /// <summary>
    /// Surrogate key of the entitlement-definition row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). It is the foreign-key target a <see cref="PlanEntitlement"/> (and a later
    /// subject entitlement) references; the stable natural key callers look up by is <see cref="Key"/>.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Stable, lower-case, dotted, globally unique generic KEY of the entitlement (docs/21 "Generic entitlement
    /// keys", e.g. <c>workspace.active.max</c>, <c>ads.disabled</c>). Immutable, so an entitlement key can
    /// never silently change meaning. Comparisons are exact (ordinal).
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Whether the entitlement is a boolean <see cref="EntitlementValueKind.Flag"/> or a numeric
    /// <see cref="EntitlementValueKind.Quota"/>. Immutable: a plan grants a value of exactly this shape and
    /// the value shape can never change under existing grants.
    /// </summary>
    public EntitlementValueKind ValueKind { get; }

    /// <summary>
    /// Human-readable, generic display name of the entitlement (informational metadata only; never an
    /// identity or authorization input). A vertical may show its own paywall copy instead (docs/21).
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>Optional generic description of the entitlement, or <see langword="null"/>. Informational only.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Whether the definition is currently offered. A retired (<see cref="Deactivate"/>d) definition stays in
    /// the catalog so existing grants and references resolve, but is no longer offered for new grants
    /// (soft delete; docs/10_DATABASE_SCHEMA.md).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When this definition was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this definition was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Defines a new, active entitlement with the given generic key, value kind and display metadata. The key
    /// is canonicalized (trimmed and lower-cased) once here so the persisted value and every later lookup stay
    /// consistent; an already-canonical key is unchanged. The caller (a deployment seeding the catalog, or a
    /// later admin flow) supplies generic, product-neutral text only (AGENTS.md).
    /// </summary>
    /// <param name="key">The generic, dotted, lower-case entitlement key (required, within <see cref="MaxKeyLength"/>).</param>
    /// <param name="valueKind">Whether the entitlement is a flag or a quota.</param>
    /// <param name="displayName">The generic display name (non-blank, within <see cref="MaxDisplayNameLength"/>).</param>
    /// <param name="description">An optional generic description (within <see cref="MaxDescriptionLength"/>); null or blank stores null.</param>
    /// <param name="createdAt">When the definition is created.</param>
    /// <exception cref="ArgumentException">The key, display name or description violates an invariant.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value kind is not a defined kind.</exception>
    public static EntitlementDefinition Define(
        string key,
        EntitlementValueKind valueKind,
        string displayName,
        string? description,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            CanonicalizeKey(key),
            valueKind,
            displayName?.Trim() ?? string.Empty,
            NormalizeDescription(description),
            isActive: true,
            createdAt,
            createdAt);

    /// <summary>
    /// Updates the generic display metadata. The key and value kind are immutable, so this never changes the
    /// entitlement's identity or value shape.
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
    /// Retires the definition (soft delete): it stays in the catalog so existing grants resolve, but is no
    /// longer offered. Idempotent — deactivating an already-inactive definition only refreshes the timestamp.
    /// </summary>
    public void Deactivate(DateTimeOffset updatedAt)
    {
        IsActive = false;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Brings a retired definition back into the offered catalog. Idempotent — reactivating an already-active
    /// definition only refreshes the timestamp.
    /// </summary>
    public void Reactivate(DateTimeOffset updatedAt)
    {
        IsActive = true;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether this definition is addressed by exactly the given key. Matching is exact (ordinal) against the
    /// stored canonical key; blank or null values match nothing.
    /// </summary>
    public bool HasKey(string? key)
        => !string.IsNullOrWhiteSpace(key) && string.Equals(Key, key, StringComparison.Ordinal);

    /// <summary>Whether this definition is the one identified by the given id. Empty ids match nothing.</summary>
    public bool HasId(Guid id) => id != Guid.Empty && Id == id;

    /// <summary>
    /// Whether the given value is a valid generic entitlement key: non-blank, within the length bound and a
    /// lower-case dotted alphanumeric shape. Validation does not mutate; callers pass an already-canonical
    /// value or use <see cref="CanonicalizeKey"/> first.
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
    /// validates it. Canonicalization is purely casing/whitespace: it never rewrites characters, so an input
    /// that is not already a valid key shape is rejected rather than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a valid key after canonicalization.</exception>
    public static string CanonicalizeKey(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidKey(canonical))
        {
            throw new ArgumentException("Key violates the entitlement key invariants.", nameof(value));
        }

        return canonical;
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
    /// Identifier-only representation that is safe for structured logs: definition id, key, value kind and
    /// active flag. The display name and description are informational free-form metadata, kept out of logs
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not free-form content).
    /// </summary>
    public override string ToString()
        => $"EntitlementDefinition {Id} key={Key} kind={ValueKind} active={IsActive}";
}
