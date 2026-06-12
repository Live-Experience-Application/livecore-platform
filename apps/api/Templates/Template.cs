using System.Text.RegularExpressions;

namespace LiveCore.Api.Templates;

/// <summary>
/// Template aggregate of the Templates module (CORE-ENT-004, "Implement template-loaded entity
/// types", fourth story of the "Entity System and Templates" epic). This is the FIRST appearance
/// of the Templates module (docs/02_ARCHITECTURE.md backend module boundaries;
/// docs/05_MODULE_CONTRACTS.md: the Templates module owns the "generic template loader, template
/// validation, template versioning" and "may not hardcode vertical behavior").
///
/// A <see cref="Template"/> is the Core-owned, product-neutral "Configurable vertical/domain
/// template" (docs/03_DOMAIN_LANGUAGE.md; csv/database_tables.csv row 19: table <c>templates</c>,
/// module Templates, scope <c>global/organization</c>, "Template registry"). It is a REGISTRY
/// entry that stores a versioned template <see cref="Definition"/> document; the loader
/// (<c>TemplateEntityTypeLoader</c>) materializes a workspace's <c>EntityType</c> rows FROM that
/// definition.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md "Template boundary"): the template
/// DEFINITION (its <c>templateKey</c>, its entity-type keys, names and attribute schemas) is
/// DATA. Core STORES and LOADS this data; at runtime the data MAY carry vertical words supplied by
/// a vertical product (see the example in docs/04_PRODUCT_BOUNDARIES.md). But this SOURCE CODE
/// contains zero vertical vocabulary and zero key/name branching: there is no
/// <c>if templateKey == "..."</c> and no switch on any definition value. Validation is generic
/// SHAPE/STRUCTURE only (<see cref="TemplateDefinitionValidator"/>); every template behaves
/// identically and is defined entirely by its stored data.
///
/// Scope (the global/organization scope of csv/database_tables.csv row 19): the
/// <see cref="OrganizationId"/> is NULLABLE. A <see langword="null"/> organization id is a GLOBAL
/// template, available to every organization; a set organization id is an ORGANIZATION-scoped
/// template, owned by and loadable only into that one tenant. This single nullable column models
/// both halves of the documented "global/organization" scope without a second table, exactly as
/// the optional <c>user_id</c> on <c>participants</c> models the linked-or-anonymous distinction
/// with one nullable column. The scope is the loader's tenant-isolation input (threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md): an org-scoped template may be loaded only into a workspace
/// of the SAME organization; a global template may be loaded into any organization's workspace.
///
/// Identity invariant: a template is addressed by a surrogate <see cref="Id"/> (UUIDv7) plus a
/// natural key of <see cref="TemplateKey"/> + <see cref="Version"/> within its scope. The
/// <see cref="TemplateKey"/> is a canonical DOT-SEPARATED slug token (each dot-separated segment
/// is a lower-case dash-separated alphanumeric slug, like a <c>Workspace.Slug</c>/<c>EntityType.TypeKey</c>
/// segment), so the dotted shape in the docs/04 example is valid while a plain single segment
/// (<c>sample-template</c>) is valid too. The dot is permitted as a generic separator
/// only; the shape carries no knowledge of any specific key (the template boundary, docs/04). The
/// <see cref="Version"/> is a positive integer enabling template versioning (docs/05); the full
/// version-transition workflow is a later concern, the version FIELD is this story.
///
/// Definition invariant: <see cref="Definition"/> is the template content as a JSON document
/// (docs/10_DATABASE_SCHEMA.md permits JSONB for flexible template-defined attributes, never for
/// authorization fields). It is validated for well-formedness AND a minimal STRUCTURAL check (a
/// top-level <c>templateKey</c> and a non-empty <c>entityTypes</c> array whose entries each carry
/// a valid key/name/schema) by <see cref="TemplateDefinitionValidator"/>, reusing the
/// <c>EntityType</c> validators. It is never written to logs (threat T7; see
/// <see cref="ToString"/>).
///
/// This story is the registry model + the entity-type loader + validation ONLY. There is NO HTTP
/// endpoint (csv/api_routes.csv defines no template route), NO template UPDATE/version-transition
/// workflow, NO entity-instance schema-conformance validation, NO visibility/search surface and
/// NO change to the <c>entity_types</c> table (the loader creates normal workspace EntityType
/// rows). Those are out of scope.
/// </summary>
public sealed class Template
{
    /// <summary>
    /// Minimum template-key length. A single-character key is too easy to collide with or
    /// mistype, so the shortest accepted key is two characters (mirrors the workspace and
    /// entity-type key bounds).
    /// </summary>
    public const int MinTemplateKeyLength = 2;

    /// <summary>
    /// Maximum template-key length. The key is a natural key used in per-scope lookups and
    /// indexes; this bound keeps it well within identifier and index limits. It is generous
    /// enough for several dot-separated segments while still bounding hostile input.
    /// </summary>
    public const int MaxTemplateKeyLength = 128;

    /// <summary>
    /// Lowest accepted template version. Versions are positive integers starting at one; a zero
    /// or negative version is rejected (docs/05_MODULE_CONTRACTS.md: template versioning).
    /// </summary>
    public const int MinVersion = 1;

    /// <summary>
    /// Overall maximum accepted definition document length — the size bound on the template
    /// definition JSON. The definition can carry many entity-type entries, so it gets a generous
    /// bound (matching the entity-type attribute-schema and Data content-block bounds) while
    /// still rejecting hostile or broken oversized input. It is a content-shape limit, not a
    /// storage byte quota.
    /// </summary>
    public const int MaxDefinitionLength = 65536;

    // Canonical template-key shape: one or more DOT-SEPARATED segments, where each segment is a
    // lower-case dash-separated alphanumeric slug (no leading, trailing or doubled dash; no
    // leading, trailing or doubled dot). This permits a dotted multi-segment key (the shape in the
    // docs/04 example) AND a plain single-segment key ("sample-template"), keeping the key
    // URL-/token-safe, predictable and free of casing ambiguity that could blur per-scope identity.
    // Crucially this is a GENERIC SHAPE rule, not a vocabulary rule: it matches any well-formed
    // dotted slug and contains no knowledge of any specific template key (the template boundary,
    // docs/04 — the template is defined by data, not by Core source). It reuses the
    // workspace/entity-type slug segment shape, extended only with the dot separator.
    private static readonly Regex _templateKeyPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private Template(
        Guid id,
        Guid? organizationId,
        string templateKey,
        int version,
        string definition,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Template id must not be empty.", nameof(id));
        }

        // A null organization id is the legitimate GLOBAL scope; only an EMPTY (all-zero) id is
        // rejected, because Guid.Empty can never address a real organization (it is never a valid
        // tenant boundary) yet is not the null/global sentinel.
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id must be null (global) or a real organization id, never empty.",
                nameof(organizationId));
        }

        if (!IsValidTemplateKey(templateKey))
        {
            throw new ArgumentException("Template key violates the template key invariants.", nameof(templateKey));
        }

        if (version < MinVersion)
        {
            throw new ArgumentException("Template version must be a positive integer.", nameof(version));
        }

        if (!TemplateDefinitionValidator.IsValidDefinition(definition))
        {
            throw new ArgumentException("Definition violates the template definition invariants.", nameof(definition));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "A template cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        TemplateKey = templateKey;
        Version = version;
        Definition = definition;
        // Timestamps are normalized to UTC so the persisted timestamptz values are
        // offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Template()
    {
        TemplateKey = null!;
        Definition = null!;
    }

    /// <summary>
    /// Surrogate key of the template row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes a load: the loader checks the
    /// template's <see cref="OrganizationId"/> scope against the target workspace's tenant
    /// (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Scope of the template (csv/database_tables.csv row 19, "global/organization"):
    /// <see langword="null"/> is a GLOBAL template available to every organization; a set value is
    /// the id of the single organization that owns the template (the nullable <c>organization_id</c>
    /// foreign key to the <c>organizations</c> table). Immutable; a template never changes scope.
    /// The loader uses this as the tenant-isolation input (threat T5).
    /// </summary>
    public Guid? OrganizationId { get; }

    /// <summary>
    /// Stable, lower-case, canonical dot-separated natural key of the template, unique with its
    /// <see cref="Version"/> WITHIN its scope (a global key+version is unique among global
    /// templates; an org key+version is unique within that organization). This is the DATA a
    /// vertical supplies to name the template; Core stores it verbatim and never branches on its
    /// value (the template boundary, docs/04). Immutable; comparisons are exact (ordinal,
    /// case-sensitive).
    /// </summary>
    public string TemplateKey { get; }

    /// <summary>
    /// Positive integer version of the template (docs/05_MODULE_CONTRACTS.md: the Templates
    /// module owns template versioning). Part of the per-scope natural key, so the same
    /// <see cref="TemplateKey"/> may exist at several versions. The version-transition workflow
    /// (promoting/retiring versions) is a later concern; this is the version FIELD only.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// The template content as a JSON document (docs/10_DATABASE_SCHEMA.md permits JSONB for
    /// flexible template-defined attributes, never for authorization fields). Validated by
    /// <see cref="TemplateDefinitionValidator"/> for well-formedness and the minimal structure the
    /// loader iterates (a <c>templateKey</c> plus a non-empty <c>entityTypes</c> array of valid
    /// entries). Treated as template-/host-supplied CONTENT, so it is never written to logs
    /// (threat T7; see <see cref="ToString"/>).
    /// </summary>
    public string Definition { get; }

    /// <summary>When this template was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this template was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// Creates a new GLOBAL template (available to every organization). The template key is
    /// canonicalized to its lower-case dotted-slug form once here so the persisted value and every
    /// later comparison stay consistent. The definition is supplied entirely as DATA — Core never
    /// inspects its vocabulary (the template boundary, docs/04).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The template key, version or definition violates an invariant.
    /// </exception>
    public static Template CreateGlobal(
        string templateKey,
        int version,
        string definition,
        DateTimeOffset createdAt)
        => Create(null, templateKey, version, definition, createdAt);

    /// <summary>
    /// Creates a new ORGANIZATION-scoped template owned by the given organization (loadable only
    /// into a workspace of that same organization; threat T5). The template key is canonicalized
    /// once here. The definition is supplied entirely as DATA.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id is empty, or the template key, version or definition violates an
    /// invariant.
    /// </exception>
    public static Template CreateForOrganization(
        Guid organizationId,
        string templateKey,
        int version,
        string definition,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Organization id must not be empty for an organization-scoped template.",
                nameof(organizationId));
        }

        return Create(organizationId, templateKey, version, definition, createdAt);
    }

    private static Template Create(
        Guid? organizationId,
        string templateKey,
        int version,
        string definition,
        DateTimeOffset createdAt)
    {
        var canonicalKey = CanonicalizeTemplateKey(templateKey);

        return new Template(
            Guid.CreateVersion7(),
            organizationId,
            canonicalKey,
            version,
            definition?.Trim() ?? string.Empty,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this template is GLOBAL (available to every organization): its
    /// <see cref="OrganizationId"/> is <see langword="null"/>.
    /// </summary>
    public bool IsGlobal => OrganizationId is null;

    /// <summary>
    /// Whether this template belongs to (is owned by) exactly the given organization. A GLOBAL
    /// template belongs to NO organization, so it returns <see langword="false"/> here even though
    /// it is loadable everywhere — "belongs to" is the OWNERSHIP question, not the loadable
    /// question (use <see cref="IsLoadableInto"/> for the loader's decision). An empty id matches
    /// nothing (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this template may be loaded into a workspace owned by the given target
    /// organization. A GLOBAL template (<see cref="IsGlobal"/>) may be loaded into ANY
    /// organization's workspace; an ORGANIZATION-scoped template may be loaded ONLY into a
    /// workspace of the SAME organization (threat T5: an org-A template can never be loaded into
    /// an org-B workspace). An empty target id matches nothing. This is the tenant-isolation gate
    /// the loader enforces before creating any entity type.
    /// </summary>
    public bool IsLoadableInto(Guid targetOrganizationId)
        => targetOrganizationId != Guid.Empty
            && (IsGlobal || OrganizationId == targetOrganizationId);

    /// <summary>
    /// Whether this template is the one identified by the given id WITHIN the given scope. The
    /// scope must match (a null <paramref name="organizationId"/> matches only a global template;
    /// a set id matches only an org template of that organization) and the id must match; an empty
    /// id matches nothing. The surrogate id is only ever honored inside its scope (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid? organizationId, Guid id)
        => OrganizationId == organizationId
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Whether the given value is a valid canonical template key: one or more dot-separated
    /// lower-case dash-separated alphanumeric segments, within the length bounds and free of
    /// control characters. Validation does not mutate; callers pass an already-canonical value or
    /// use <see cref="CanonicalizeTemplateKey"/> first. This is a generic SHAPE check with no
    /// knowledge of any specific template vocabulary (the template boundary, docs/04).
    /// </summary>
    public static bool IsValidTemplateKey(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length >= MinTemplateKeyLength
            && value.Length <= MaxTemplateKeyLength
            && _templateKeyPattern.IsMatch(value);

    /// <summary>
    /// Canonicalizes a template key to its stored form (trimmed and lower-cased using the
    /// invariant culture) and validates it. Canonicalization is purely casing/whitespace: it never
    /// rewrites characters, so an input that is not already a valid dotted-slug shape is rejected
    /// rather than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a valid template key after canonicalization.
    /// </exception>
    public static string CanonicalizeTemplateKey(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidTemplateKey(canonical))
        {
            throw new ArgumentException("Template key violates the template key invariants.", nameof(value));
        }

        return canonical;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: template id, scope
    /// (organization id or the literal <c>global</c>), template key and version. The
    /// <see cref="Definition"/> content is excluded so template-/host-supplied content cannot leak
    /// through logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not
    /// free-form content). The template key is a canonical slug identifier (not free-form content),
    /// so it is safe to log, like the workspace slug.
    /// </summary>
    public override string ToString()
    {
        var scope = OrganizationId is { } organizationId
            ? organizationId.ToString()
            : "global";
        return $"Template {Id} scope={scope} key={TemplateKey} v={Version}";
    }
}
