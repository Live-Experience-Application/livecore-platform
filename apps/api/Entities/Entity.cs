// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Entities;

/// <summary>
/// Entity aggregate of the Entities module (CORE-ENT-002, the second story of the "Entity
/// System and Templates" epic).
///
/// An <see cref="Entity"/> is the Core-owned, product-neutral "Generic domain object"
/// (docs/03_DOMAIN_LANGUAGE.md; docs/05_MODULE_CONTRACTS.md: the Entities module owns "generic
/// entities" but may not implement any vertical-specific entity behavior directly;
/// csv/database_tables.csv: table <c>entities</c>, module Entities, scope <c>workspace</c>,
/// "Generic objects"). It is a single INSTANCE of an <see cref="EntityType"/> (the
/// template-defined type built in CORE-ENT-001): the type is the generic kind, the entity is one
/// concrete object of that kind. The instance's human label and its actual attribute data are
/// supplied at runtime as DATA, never hardcoded in Core source.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md "Template boundary"): Core may STORE
/// entity DATA, but the content must be DATA, not hardcoded source language. So at runtime a
/// vertical template/runtime MAY put vertical words into <see cref="Name"/> or
/// <see cref="AttributeValues"/>, and Core merely persists them. But this SOURCE CODE contains
/// zero vertical vocabulary and zero type-specific logic: there is no <c>if name == "..."</c>
/// branch and no switch on any entity name or type. Every entity behaves identically; the entity
/// is defined entirely by its stored data and its <see cref="EntityTypeId"/>.
///
/// Tenant + workspace boundary: csv/database_tables.csv scopes <c>entities</c> to
/// <c>workspace</c>. So an entity is workspace-scoped and tenant-scoped, carrying both
/// <see cref="OrganizationId"/> (the tenant; checked before the workspace boundary) and
/// <see cref="WorkspaceId"/>, mirroring <c>EntityType</c> and <c>ContentBlock</c>
/// (docs/10_DATABASE_SCHEMA.md: tenant-scoped tables include <c>organization_id</c>,
/// workspace-scoped tables include <c>workspace_id</c>; threat T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). An entity belongs to exactly one organization and one
/// workspace for its whole lifetime; it never crosses to another tenant or workspace. The
/// surrogate <see cref="Id"/> alone never authorizes access: every lookup is scoped by
/// <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's entity can never
/// be reached through another workspace's or another tenant's id (threat T5; T1 broken
/// object-level authorization).
///
/// Type linkage: an entity is an instance OF an entity type, so it carries an
/// <see cref="EntityTypeId"/> that references <c>entity_types(id)</c> (the surrogate id minted in
/// CORE-ENT-001). The simple foreign key guarantees the type EXISTS but NOT that it is in the
/// SAME workspace — exactly like <c>ContentBlock.SceneId</c> does not DB-enforce that the scene
/// is in the same workspace. Enforcing that the type is in the entity's own workspace is the
/// responsibility of the future create-entity application flow (a later endpoint story), which
/// resolves the type through the workspace-scoped <c>IEntityTypeRepository.FindByIdAsync(org, ws,
/// typeId)</c> BEFORE creating the entity; the aggregate cannot perform that cross-aggregate
/// check itself. This mirrors the established ContentBlock/scene_id precedent and is the
/// documented carry-over for the create-entity endpoint story (CORE-ENT-005's endpoint work / a
/// later story); it is NOT silently ignored — it is a same-workspace coupling deferred to the
/// application layer.
///
/// Identity invariant: an entity has NO natural key — it is identified only by its surrogate
/// <see cref="Id"/> (UUIDv7), so there is deliberately no name uniqueness: two entities in one
/// workspace may share a name (and even a type) and stay fully isolated, each addressed by its
/// own id.
///
/// Name invariant: <see cref="Name"/> is the instance's human label — bounded, non-blank and
/// free of control characters (like <see cref="EntityType.DisplayName"/>). It is template-/host-
/// supplied DATA: never an identity, never an authorization input, never a tenant boundary, and
/// never written to logs (threat T7; see <see cref="ToString"/>).
///
/// Attribute-values invariant: <see cref="AttributeValues"/> is the instance's actual attribute
/// data, stored as a JSON document (docs/10_DATABASE_SCHEMA.md permits JSONB for flexible
/// template-defined attributes, never for authorization fields). It is treated as template-/host-
/// supplied CONTENT, so it is validated only for JSON WELL-FORMEDNESS and a size bound (reusing
/// the System.Text.Json parse pattern via <see cref="AttributeValuesValidator"/>) and is never
/// written to logs (threat T7). It is deliberately NOT validated against the
/// <see cref="EntityType"/>'s attribute SCHEMA: schema-conformance validation (checking the
/// instance's values against the type's declared attribute definitions) is template-engine
/// territory and belongs to the template loader / CORE-ENT-004, not to this bare instance model.
///
/// There is deliberately no lifecycle status, no visibility surface, no relationships and no
/// template loading here. The Entities module owns "generic entities, entity types, entity
/// relationships" (docs/05_MODULE_CONTRACTS.md); this story is the entity INSTANCE model only.
/// Entity relationships (CORE-ENT-003), template loading and schema-conformance validation
/// (CORE-ENT-004) and entity search with visibility filtering (CORE-ENT-005) are later stories
/// and are deliberately omitted.
/// </summary>
public sealed class Entity
{
    /// <summary>
    /// Maximum accepted name length. The name is the instance's human label — informational
    /// metadata; longer values are rejected as hostile or broken input (mirrors
    /// <see cref="EntityType.MaxDisplayNameLength"/> and the workspace/scene display bounds).
    /// </summary>
    public const int MaxNameLength = 256;

    /// <summary>
    /// Overall maximum accepted attribute-values length — the size bound on the instance's
    /// attribute JSON document. The attribute values are the flexible part of the instance and
    /// are the largest legitimate field, so they get a generous bound (matching the entity-type
    /// attribute-schema bound) while still rejecting hostile or broken oversized input. It is a
    /// content-shape limit, not a storage byte quota.
    /// </summary>
    public const int MaxAttributeValuesLength = 65536;

    private Entity(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name,
        string attributeValues,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (entityTypeId == Guid.Empty)
        {
            throw new ArgumentException("Entity type id must not be empty.", nameof(entityTypeId));
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        if (!IsValidAttributeValues(attributeValues))
        {
            throw new ArgumentException(
                "Attribute values violate the attribute values invariants.",
                nameof(attributeValues));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An entity cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        EntityTypeId = entityTypeId;
        Name = name;
        AttributeValues = attributeValues;
        // Timestamps are normalized to UTC so the persisted timestamptz values are
        // offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private Entity()
    {
        Name = null!;
        AttributeValues = null!;
    }

    /// <summary>
    /// Surrogate key of the entity row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes access: lookups are always
    /// scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the entity: the id of the organization that owns the workspace this
    /// entity belongs to (the <c>organization_id</c> foreign key to the <c>organizations</c>
    /// table). Immutable; an entity never crosses to another organization. The organization
    /// boundary is checked before the workspace boundary, so this column lets isolation be
    /// enforced at the row level (threat T5; docs/06_AUTHORIZATION_MATRIX.md authorization
    /// principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the entity: the id of the workspace this entity belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; an entity
    /// never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Type linkage of the entity: the id of the <see cref="EntityType"/> this entity is an
    /// instance of (the <c>entity_type_id</c> foreign key to the <c>entity_types</c> table).
    /// Immutable; an entity is created as an instance of exactly one type and never changes type.
    /// The simple foreign key guarantees the type EXISTS but not that it is in the same
    /// workspace; the future create-entity application flow resolves the type through a
    /// workspace-scoped lookup to enforce that coupling (mirrors <c>ContentBlock.SceneId</c>).
    /// </summary>
    public Guid EntityTypeId { get; }

    /// <summary>
    /// Human-readable label of the entity instance. Informational, template-/host-supplied DATA
    /// only: never an identity, never an authorization input, never a tenant boundary, and never
    /// written to logs (threat T7; see <see cref="ToString"/>).
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// The instance's actual attribute data, stored as a JSON document
    /// (docs/10_DATABASE_SCHEMA.md permits JSONB for flexible template-defined attributes, never
    /// for authorization fields). Treated as template-/host-supplied CONTENT: validated only for
    /// JSON well-formedness and a size bound here (validating the values against the
    /// <see cref="EntityType"/>'s declared attribute schema is the template engine / CORE-ENT-004),
    /// and never written to logs (threat T7; see <see cref="ToString"/>).
    /// </summary>
    public string AttributeValues { get; private set; }

    /// <summary>When this entity was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this entity was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a new entity in the given workspace (owned by the given organization) as an
    /// instance of the given entity type. The caller (a later create-entity endpoint) supplies
    /// the tenant, workspace and type ids and the name and attribute values entirely as DATA —
    /// Core never inspects the name's or values' vocabulary (the template boundary, docs/04). The
    /// caller is responsible for having resolved the type through a workspace-scoped lookup so the
    /// type is in the entity's own workspace; the aggregate enforces only the structural
    /// invariants (non-empty ids, a valid name, well-formed bounded attribute values).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or entity type id is empty, or the name or attribute
    /// values violate an invariant.
    /// </exception>
    public static Entity Create(
        Guid organizationId,
        Guid workspaceId,
        Guid entityTypeId,
        string name,
        string attributeValues,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            entityTypeId,
            name?.Trim() ?? string.Empty,
            attributeValues?.Trim() ?? string.Empty,
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this entity belongs to exactly the given organization. Empty ids match nothing.
    /// This is the tenant-boundary check, checked before the workspace boundary: an entity
    /// belongs only to the organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this entity belongs to exactly the given workspace. Empty ids match nothing. This
    /// is the workspace-boundary check: an entity belongs only to the workspace it names, never
    /// to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this entity is an instance of exactly the given entity type. Empty ids match
    /// nothing. This is the type-linkage check: an entity is an instance only of the type it
    /// names, never of any other.
    /// </summary>
    public bool IsOfType(Guid entityTypeId)
        => entityTypeId != Guid.Empty && EntityTypeId == entityTypeId;

    /// <summary>
    /// Whether this entity is the one identified by the given id WITHIN the given organization
    /// and workspace. All three must match; an empty id matches nothing. An entity with the same
    /// id under a different tenant or workspace (which cannot happen for a single row but guards
    /// the caller's intent) returns <see langword="false"/>: the surrogate id is only ever
    /// honored inside its tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Renames the entity's human label. The organization, workspace, id, entity type and
    /// attribute values are immutable here, so renaming never moves the entity, never changes the
    /// tenant boundary and never changes the type linkage (threat T5). An invalid name is
    /// rejected with NO mutation, leaving the prior name intact.
    /// </summary>
    /// <exception cref="ArgumentException">The new name violates an invariant.</exception>
    public void Rename(string name, DateTimeOffset updatedAt)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (!IsValidName(trimmed))
        {
            throw new ArgumentException("Name violates the name invariants.", nameof(name));
        }

        Name = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Replaces the instance's attribute values. The organization, workspace, id and entity type
    /// are immutable here, so redefining the values never moves the entity and never changes its
    /// type linkage (threat T5). The new values must be well-formed JSON within the size bound; an
    /// invalid document is rejected with NO mutation, leaving the prior values intact. Only JSON
    /// well-formedness is checked here — validating the values against the entity type's attribute
    /// schema is the template engine / CORE-ENT-004.
    /// </summary>
    /// <exception cref="ArgumentException">The new attribute values violate an invariant.</exception>
    public void RedefineAttributeValues(string attributeValues, DateTimeOffset updatedAt)
    {
        var trimmed = attributeValues?.Trim() ?? string.Empty;
        if (!IsValidAttributeValues(trimmed))
        {
            throw new ArgumentException(
                "Attribute values violate the attribute values invariants.",
                nameof(attributeValues));
        }

        AttributeValues = trimmed;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>
    /// Whether the given value is a valid name: non-blank, within the length bound and free of
    /// control characters.
    /// </summary>
    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= MaxNameLength
            && !value.Any(char.IsControl);

    /// <summary>
    /// Whether the given value is a valid attribute-values document: non-blank, within the size
    /// bound and WELL-FORMED JSON (delegated to <see cref="AttributeValuesValidator"/>). Only
    /// well-formedness is checked, never the values against the entity type's attribute schema
    /// (that is the template engine, CORE-ENT-004).
    /// </summary>
    public static bool IsValidAttributeValues(string? value)
        => AttributeValuesValidator.IsValidValues(value);

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: entity id, organization
    /// id, workspace id and entity type id. The name and the attribute values are excluded so
    /// template-/host-supplied content cannot leak through logs (threat T7 in
    /// docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not free-form content).
    /// </summary>
    public override string ToString()
        => $"Entity {Id} org={OrganizationId} ws={WorkspaceId} type={EntityTypeId}";
}
