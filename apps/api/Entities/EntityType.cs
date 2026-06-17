// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.Entities;

/// <summary>
/// EntityType aggregate of the Entities module (CORE-ENT-001, the first story of the
/// "Entity System and Templates" epic).
///
/// An <see cref="EntityType"/> is the Core-owned, product-neutral "Template-defined type
/// for an entity" (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md: the Entities
/// module owns "entity types" but may not implement any vertical-specific entity behavior
/// directly; csv/database_tables.csv: table <c>entity_types</c>, scope
/// <c>workspace/template</c>, "Template-defined types"). It is the generic, DATA-DRIVEN
/// definition of a kind of entity: the actual type vocabulary (its key, its display name
/// and the shape of its template-defined attributes) is supplied at runtime as DATA, never
/// hardcoded in Core source.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md "Template boundary"): Core may
/// STORE template-defined type definitions, but the template content must be DATA, not
/// hardcoded source language. So at runtime a vertical template MAY put vertical words into
/// the <see cref="TypeKey"/>, <see cref="DisplayName"/> or <see cref="AttributeSchema"/>
/// (for example a template's "entityTypes" array), and Core merely persists them. But this
/// SOURCE CODE contains zero vertical vocabulary and zero type-specific logic: there is no
/// <c>if key == "..."</c> branch and no switch on any type name. Every type behaves
/// identically; the type is defined entirely by its stored data. Loading and validating a
/// type AGAINST a template (the template registry, the attribute-schema-validation engine,
/// the <c>template_id</c> linkage) is the separate Templates module / CORE-ENT-004
/// ("template-loaded entity types"); there is no templates table yet, so this story carries
/// NO template linkage and validates the attribute schema only for JSON well-formedness.
///
/// Tenant + workspace boundary: csv/database_tables.csv scopes <c>entity_types</c> to
/// <c>workspace/template</c>. The "template" half is the later CORE-ENT-004 template
/// loading; the workspace half is THIS story. So an entity type is workspace-scoped and
/// tenant-scoped, carrying both <see cref="OrganizationId"/> (the tenant; checked before
/// the workspace boundary) and <see cref="WorkspaceId"/>, mirroring <c>Scene</c>
/// (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). An entity type belongs to exactly one organization
/// and one workspace for its whole lifetime; it never crosses to another tenant or
/// workspace. The surrogate <see cref="Id"/> alone never authorizes access: every lookup is
/// scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's
/// entity type can never be reached through another workspace's or another tenant's id
/// (threat T5; T1 broken object-level authorization).
///
/// Identity invariant: a type is addressed inside its workspace by two keys. The surrogate
/// <see cref="Id"/> (UUIDv7) is the row's own key (the value entity instances will reference
/// in CORE-ENT-002). The <see cref="TypeKey"/> is the stable, canonical natural key, unique
/// WITHIN the workspace (a workspace cannot define the same type twice) but NOT globally:
/// the same key may legitimately exist in two different workspaces. The key is canonicalized
/// to a lower-case, dash-separated slug shape once at creation so a near-match or re-cased
/// key can never silently address a different type; comparisons are exact (ordinal,
/// case-sensitive) against the stored canonical value.
///
/// Display invariant: <see cref="DisplayName"/> is human-facing metadata only — never an
/// identity, never an authorization input, never a tenant boundary. Two types may share a
/// display name while staying fully isolated because their organization id, workspace id,
/// id and key differ.
///
/// Attribute-schema invariant: <see cref="AttributeSchema"/> is the flexible,
/// template-defined attribute definitions of this type, stored as a JSON document
/// (docs/10_DATABASE_SCHEMA.md permits JSONB for flexible template-defined attributes, never
/// for authorization fields). It is treated as host-/template-supplied CONTENT, so it is
/// validated only for JSON WELL-FORMEDNESS and a size bound (reusing the System.Text.Json
/// parse pattern from <c>ContentValidator</c>) and is never written to logs (threat T7; see
/// <see cref="ToString"/>). Full JSON-schema validation against a template is the Templates
/// module / CORE-ENT-004, not here.
///
/// There is deliberately no lifecycle status, no visibility surface and no entity instances
/// here. The Entities module owns "generic entities, entity types, entity relationships"
/// (docs/05_MODULE_CONTRACTS.md); this story is the TYPE model only. Entity instances
/// (CORE-ENT-002), relationships (CORE-ENT-003), template loading (CORE-ENT-004) and search
/// with visibility filtering (CORE-ENT-005) are later stories and are deliberately omitted.
/// </summary>
public sealed class EntityType
{
    /// <summary>
    /// Minimum type-key length. A single-character type key is too easy to collide with or
    /// mistype, so the shortest accepted key is two characters (mirrors the workspace and
    /// organization slug bounds).
    /// </summary>
    public const int MinTypeKeyLength = 2;

    /// <summary>
    /// Maximum type-key length. The key is a natural key used in per-workspace lookups and
    /// indexes; this bound keeps it well within identifier and index limits (mirrors the
    /// workspace slug bound).
    /// </summary>
    public const int MaxTypeKeyLength = 64;

    /// <summary>
    /// Maximum accepted display-name length. The display name is informational metadata;
    /// longer values are rejected as hostile or broken input (mirrors the workspace and
    /// scene display bounds).
    /// </summary>
    public const int MaxDisplayNameLength = 256;

    /// <summary>
    /// Overall maximum accepted attribute-schema length — the size bound on the
    /// template-defined attribute JSON document. The schema is the flexible part of the type
    /// and is the largest legitimate field, so it gets a generous bound (matching the Data
    /// content-block bound) while still rejecting hostile or broken oversized input. It is a
    /// content-shape limit, not a storage byte quota.
    /// </summary>
    public const int MaxAttributeSchemaLength = 65536;

    // Canonical type-key shape: lower-case letters/digits in dash-separated groups (no
    // leading, trailing or doubled dash). This keeps the key URL-safe, predictable and free
    // of control characters or casing ambiguity that could blur per-workspace identity. It is
    // identical to the workspace/organization slug shape so the natural keys behave
    // consistently. Crucially this is a GENERIC SHAPE rule, not a vocabulary rule: it matches
    // any well-formed slug and contains no knowledge of any specific type name (the template
    // boundary, docs/04 — the type is defined by data, not by Core source).
    private static readonly Regex _typeKeyPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private EntityType(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        string displayName,
        string attributeSchema,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity type id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (!IsValidTypeKey(typeKey))
        {
            throw new ArgumentException("Type key violates the type key invariants.", nameof(typeKey));
        }

        if (!IsValidDisplayName(displayName))
        {
            throw new ArgumentException("Display name violates the display name invariants.", nameof(displayName));
        }

        if (!IsValidAttributeSchema(attributeSchema))
        {
            throw new ArgumentException(
                "Attribute schema violates the attribute schema invariants.",
                nameof(attributeSchema));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An entity type cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        TypeKey = typeKey;
        DisplayName = displayName;
        AttributeSchema = attributeSchema;
        // Timestamps are normalized to UTC so the persisted timestamptz values are
        // offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private EntityType()
    {
        TypeKey = null!;
        DisplayName = null!;
        AttributeSchema = null!;
    }

    /// <summary>
    /// Surrogate key of the entity type row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). This is the value entity instances will reference
    /// through their type foreign key (CORE-ENT-002). On its own it never authorizes access:
    /// lookups are always scoped by <see cref="OrganizationId"/> and
    /// <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the entity type: the id of the organization that owns the
    /// workspace this type belongs to (the <c>organization_id</c> foreign key to the
    /// <c>organizations</c> table). Immutable; an entity type never crosses to another
    /// organization. The organization boundary is checked before the workspace boundary, so
    /// this column lets isolation be enforced at the row level (threat T5;
    /// docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the entity type: the id of the workspace this type belongs to
    /// (the <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; an
    /// entity type never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Stable, lower-case, canonical natural key of the type, unique WITHIN its workspace
    /// (not globally). This is the DATA a vertical template supplies to define the type;
    /// Core stores it verbatim and never branches on its value (the template boundary,
    /// docs/04). Immutable, URL-safe and used for per-workspace lookups; comparisons are
    /// exact (ordinal, case-sensitive).
    /// </summary>
    public string TypeKey { get; }

    /// <summary>
    /// Human-readable display label of the type. Informational, template-supplied metadata
    /// only: never an identity, never an authorization input, never a tenant boundary.
    /// </summary>
    public string DisplayName { get; private set; }

    /// <summary>
    /// The template-defined attribute definitions of this type, stored as a JSON document
    /// (docs/10_DATABASE_SCHEMA.md permits JSONB for flexible template-defined attributes,
    /// never for authorization fields). Treated as template-/host-supplied CONTENT: validated
    /// only for JSON well-formedness and a size bound here (full schema validation against a
    /// template is CORE-ENT-004), and never written to logs (threat T7; see
    /// <see cref="ToString"/>).
    /// </summary>
    public string AttributeSchema { get; private set; }

    /// <summary>When this entity type was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this entity type was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new entity type in the given workspace (owned by the given organization).
    /// The type key is canonicalized to its lower-case slug form once here so the persisted
    /// value and every later comparison stay consistent; an already-canonical key is
    /// unchanged. The caller (a later template-load or admin path) supplies the key, display
    /// name and attribute schema entirely as DATA — Core never inspects the key's vocabulary
    /// (the template boundary, docs/04).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id or workspace id is empty, or the type key, display name or
    /// attribute schema violates an invariant.
    /// </exception>
    public static EntityType Create(
        Guid organizationId,
        Guid workspaceId,
        string typeKey,
        string displayName,
        string attributeSchema,
        DateTimeOffset createdAt)
    {
        var canonicalKey = CanonicalizeTypeKey(typeKey);

        return new EntityType(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            canonicalKey,
            displayName?.Trim() ?? string.Empty,
            attributeSchema?.Trim() ?? string.Empty,
            createdAt,
            createdAt);
    }

    /// <summary>
    /// Whether this entity type belongs to exactly the given organization. Empty ids match
    /// nothing. This is the tenant-boundary check, checked before the workspace boundary: an
    /// entity type belongs only to the organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this entity type belongs to exactly the given workspace. Empty ids match
    /// nothing. This is the workspace-boundary check: an entity type belongs only to the
    /// workspace it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this entity type is the one identified by the given id WITHIN the given
    /// organization and workspace. All three must match; an empty id matches nothing. A type
    /// with the same id under a different tenant or workspace (which cannot happen for a
    /// single row but guards the caller's intent) returns <see langword="false"/>: the
    /// surrogate id is only ever honored inside its tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Whether this entity type is addressed by exactly the given type key WITHIN the given
    /// organization and workspace. Matching is exact (ordinal, case-sensitive) against the
    /// stored canonical key; a blank or null key, or a different tenant/workspace, matches
    /// nothing. The same key in another workspace is a different type and is never matched
    /// here (threat T5).
    /// </summary>
    public bool HasKeyIn(Guid organizationId, Guid workspaceId, string? typeKey)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && !string.IsNullOrWhiteSpace(typeKey)
            && string.Equals(TypeKey, typeKey, StringComparison.Ordinal);

    /// <summary>
    /// Renames the type's display label. The organization, workspace, id, type key and
    /// attribute schema are immutable here, so renaming never moves the type, never changes
    /// the tenant boundary and never changes the natural key (threat T5). An invalid name is
    /// rejected with NO mutation, leaving the prior display name intact.
    /// </summary>
    /// <exception cref="ArgumentException">The new display name violates an invariant.</exception>
    public void Rename(string displayName, DateTimeOffset updatedAt)
    {
        var trimmed = displayName?.Trim() ?? string.Empty;
        if (!IsValidDisplayName(trimmed))
        {
            throw new ArgumentException("Display name violates the display name invariants.", nameof(displayName));
        }

        DisplayName = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Replaces the template-defined attribute schema of the type. The organization,
    /// workspace, id and type key are immutable here, so redefining the schema never moves
    /// the type and never changes the natural key (threat T5). The new schema must be
    /// well-formed JSON within the size bound; an invalid schema is rejected with NO
    /// mutation, leaving the prior schema intact. Only JSON well-formedness is checked here —
    /// validating the schema against a template is CORE-ENT-004.
    /// </summary>
    /// <exception cref="ArgumentException">The new attribute schema violates an invariant.</exception>
    public void RedefineAttributeSchema(string attributeSchema, DateTimeOffset updatedAt)
    {
        var trimmed = attributeSchema?.Trim() ?? string.Empty;
        if (!IsValidAttributeSchema(trimmed))
        {
            throw new ArgumentException(
                "Attribute schema violates the attribute schema invariants.",
                nameof(attributeSchema));
        }

        AttributeSchema = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid canonical type key: lower-case, dash-separated
    /// alphanumeric groups, within the length bounds and free of control characters.
    /// Validation does not mutate; callers pass an already-canonical value or use
    /// <see cref="CanonicalizeTypeKey"/> first. This is a generic SHAPE check with no
    /// knowledge of any specific type vocabulary (the template boundary, docs/04).
    /// </summary>
    public static bool IsValidTypeKey(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length >= MinTypeKeyLength
            && value.Length <= MaxTypeKeyLength
            && _typeKeyPattern.IsMatch(value);

    /// <summary>
    /// Whether the given value is a valid display name: non-blank, within the length bound
    /// and free of control characters.
    /// </summary>
    public static bool IsValidDisplayName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxDisplayNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid attribute schema: non-blank, within the size bound
    /// and WELL-FORMED JSON (delegated to <see cref="AttributeSchemaValidator"/>). Only
    /// well-formedness is checked, never the JSON schema against a template (CORE-ENT-004).
    /// </summary>
    public static bool IsValidAttributeSchema(string? value)
        => AttributeSchemaValidator.IsValidSchema(value);

    /// <summary>
    /// Canonicalizes a type key to its stored form (trimmed and lower-cased using the
    /// invariant culture) and validates it. Canonicalization is purely casing/whitespace: it
    /// never rewrites characters, so an input that is not already a valid slug shape is
    /// rejected rather than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a valid type key after canonicalization.
    /// </exception>
    public static string CanonicalizeTypeKey(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidTypeKey(canonical))
        {
            throw new ArgumentException("Type key violates the type key invariants.", nameof(value));
        }

        return canonical;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: entity type id,
    /// organization id, workspace id and type key. The display name and the attribute schema
    /// are excluded so template-/host-supplied content cannot leak through logs (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not free-form content). The
    /// type key is a canonical slug identifier (not free-form content), so it is safe to log,
    /// like the workspace slug.
    /// </summary>
    public override string ToString()
        => $"EntityType {Id} org={OrganizationId} ws={WorkspaceId} key={TypeKey}";
}
