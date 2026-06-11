using System.Text.RegularExpressions;

namespace LiveCore.Api.Organizations;

/// <summary>
/// Organization aggregate of the Organizations module (CORE-ID-003).
///
/// An <see cref="Organization"/> is the Core-owned, product-neutral tenant
/// root (docs/03_DOMAIN_LANGUAGE.md: "Tenant or owner boundary";
/// csv/database_tables.csv: table <c>organizations</c>, scope <c>global</c>,
/// "Tenant root"). Every tenant-scoped table later hangs off an
/// <c>organization_id</c> that points at one of these rows
/// (docs/10_DATABASE_SCHEMA.md, threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md), so the organization is the anchor of
/// the entire tenant boundary. It stores no vertical product language: it is
/// a generic owner boundary only (docs/04_PRODUCT_BOUNDARIES.md, ADR 0003).
///
/// Instances are only ever in a valid state: the constructor rejects every
/// argument that violates an invariant, mirroring <c>UserProfile</c>
/// (CORE-ID-002).
///
/// Identity invariant: an organization is addressed by two keys. The
/// surrogate <see cref="Id"/> (UUIDv7) is the foreign-key target used by
/// tenant-scoped tables; the <see cref="Slug"/> is the stable, human-meaning
/// natural key used for lookups and for matching the organization claim values
/// the identity provider asserts on a token (CORE-ID-005 will resolve those
/// claims against this slug). The slug is immutable for the lifetime of the
/// organization, lower-case, and globally unique, so a near-match or a
/// re-used slug can never silently address a different tenant (threat T5).
/// All slug comparisons are exact (ordinal, case-sensitive); the lower-casing
/// happens once at creation so the persisted value is already canonical.
///
/// Display invariant: <see cref="Name"/> is human-facing metadata only. It is
/// never an identity, never an authorization input, and never a tenant
/// boundary: two organizations may legitimately share a display name while
/// staying fully isolated because the slug and id differ.
/// </summary>
public sealed class Organization
{
    /// <summary>
    /// Minimum slug length. A single-character tenant key is too easy to
    /// collide with or mistype, so the shortest accepted slug is two
    /// characters.
    /// </summary>
    public const int MinSlugLength = 2;

    /// <summary>
    /// Maximum slug length. The slug is part of URLs and of the
    /// organization claim values matched at the tenant boundary; this bound
    /// keeps it well within identifier and index limits.
    /// </summary>
    public const int MaxSlugLength = 64;

    /// <summary>
    /// Maximum accepted display name length. Names are informational
    /// metadata; longer values are rejected as hostile or broken input.
    /// </summary>
    public const int MaxNameLength = 256;

    // Canonical slug shape: lower-case letters/digits in dash-separated
    // groups (no leading, trailing or doubled dash). This keeps the tenant
    // key URL-safe, predictable and free of control characters or casing
    // ambiguity that could blur the tenant boundary (threat T5).
    private static readonly Regex _slugPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private Organization(
        Guid id,
        string slug,
        string name,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(id));
        }

        if (!IsValidSlug(slug))
        {
            throw new ArgumentException("Slug violates the slug invariants.", nameof(slug));
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An organization cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        Slug = slug;
        Name = name;
        // Timestamps are normalized to UTC so the persisted timestamptz
        // values are offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Organization()
    {
        Slug = null!;
        Name = null!;
    }

    /// <summary>
    /// Surrogate key of the organization row (UUID version 7, time-ordered
    /// per docs/10_DATABASE_SCHEMA.md). This is the value tenant-scoped tables
    /// reference through their <c>organization_id</c> column.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Stable, lower-case, globally unique natural key of the tenant. It is
    /// immutable, URL-safe, and the value matched against the organization
    /// claims of a token at the tenant boundary (CORE-ID-005). Comparisons
    /// are exact (ordinal, case-sensitive).
    /// </summary>
    public string Slug { get; }

    /// <summary>
    /// Human-readable display name of the organization. Informational
    /// metadata only: never an identity, never an authorization input, never
    /// a tenant boundary.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>When this organization was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this organization was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new organization. The slug is canonicalized to lower case
    /// once here so the persisted value and every later comparison stay
    /// consistent; an already-canonical slug is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The slug or name violates an invariant.
    /// </exception>
    public static Organization Create(string slug, string name, DateTimeOffset createdAt)
    {
        var canonicalSlug = CanonicalizeSlug(slug);

        return new Organization(
            Guid.CreateVersion7(),
            canonicalSlug,
            name?.Trim() ?? string.Empty,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this organization is addressed by exactly the given slug.
    /// Matching is exact (ordinal, case-sensitive) against the stored
    /// canonical slug: a near-match or different-cased value must never cross
    /// the tenant boundary (threat T5). Blank or null values match nothing.
    /// </summary>
    public bool HasSlug(string? slug)
        => !string.IsNullOrWhiteSpace(slug)
            && string.Equals(Slug, slug, StringComparison.Ordinal);

    /// <summary>
    /// Whether this organization is the one identified by the given id. Blank
    /// (empty) ids match nothing.
    /// </summary>
    public bool HasId(Guid id) => id != Guid.Empty && Id == id;

    /// <summary>
    /// Updates the display name. The slug and id are immutable, so renaming
    /// never moves the organization or changes the tenant boundary.
    /// </summary>
    /// <exception cref="ArgumentException">The new name violates an invariant.</exception>
    public void Rename(string name, DateTimeOffset updatedAt)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (!IsValidName(trimmedName))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        Name = trimmedName;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid canonical slug: lower-case,
    /// dash-separated alphanumeric groups, within the length bounds and free
    /// of control characters. Validation does not mutate; callers pass an
    /// already-canonical value or use <see cref="CanonicalizeSlug"/> first.
    /// </summary>
    public static bool IsValidSlug(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length >= MinSlugLength
            && value.Length <= MaxSlugLength
            && _slugPattern.IsMatch(value);

    /// <summary>
    /// Whether the given value is a valid display name: non-blank, within the
    /// length bound and free of control characters.
    /// </summary>
    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Canonicalizes a slug to its stored form (trimmed and lower-cased using
    /// the invariant culture) and validates it. Canonicalization is purely
    /// casing/whitespace: it never rewrites characters, so an input that is
    /// not already a valid slug shape is rejected rather than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a valid slug after canonicalization.
    /// </exception>
    public static string CanonicalizeSlug(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidSlug(canonical))
        {
            throw new ArgumentException("Slug violates the slug invariants.", nameof(value));
        }

        return canonical;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs:
    /// organization id and slug. The display name is excluded so it cannot
    /// leak through logs (threat T7: logs carry identifiers, not free-form
    /// metadata content).
    /// </summary>
    public override string ToString() => $"Organization {Id} slug={Slug}";
}
