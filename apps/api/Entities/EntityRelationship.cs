// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Text.RegularExpressions;

namespace LiveCore.Api.Entities;

/// <summary>
/// EntityRelationship aggregate of the Entities module (CORE-ENT-003, the third story of the
/// "Entity System and Templates" epic).
///
/// An <see cref="EntityRelationship"/> is a Core-owned, product-neutral GRAPH EDGE between two
/// <see cref="Entity"/> instances (csv/database_tables.csv: table <c>entity_relationships</c>,
/// module Entities, scope <c>workspace</c>, "Graph edges"; docs/05_MODULE_CONTRACTS.md: the
/// Entities module owns "entity relationships" but may not implement any vertical-specific entity
/// behavior directly). It connects a SOURCE entity to a TARGET entity and carries a generic
/// <see cref="RelationshipKind"/> — the DATA-driven label of the edge. The edge is DIRECTED
/// (source -&gt; target): a directed edge is the general graph model, so it can express both
/// symmetric relationships (a vertical creates the reverse edge as a second row) and asymmetric
/// ones (only one direction stored); an undirected pair would be a special case that cannot
/// represent asymmetric edges, so the general directed model is chosen.
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md "Template boundary"): the relationship
/// KIND is DATA, supplied at runtime by a vertical template/host. So at runtime a vertical MAY put
/// vertical words into <see cref="RelationshipKind"/>, and Core merely stores and compares them as
/// an opaque token. But this SOURCE CODE contains zero vertical vocabulary and zero kind-specific
/// logic: there is no <c>if kind == "..."</c> branch and no switch on any relationship name. Every
/// relationship behaves identically; the edge is defined entirely by its endpoints and its stored
/// kind. The kind is validated only for a generic SHAPE (a bounded, canonical slug), exactly like
/// <see cref="EntityType.TypeKey"/> — Core never inspects its vocabulary.
///
/// Tenant + workspace boundary: csv/database_tables.csv scopes <c>entity_relationships</c> to
/// <c>workspace</c>. So a relationship is workspace-scoped and tenant-scoped, carrying both
/// <see cref="OrganizationId"/> (the tenant; checked before the workspace boundary) and
/// <see cref="WorkspaceId"/>, mirroring <see cref="Entity"/> (docs/10_DATABASE_SCHEMA.md:
/// tenant-scoped tables include <c>organization_id</c>, workspace-scoped tables include
/// <c>workspace_id</c>; threat T5 in docs/07_SECURITY_THREAT_MODEL.md). A relationship belongs to
/// exactly one organization and one workspace for its whole lifetime; it never crosses to another
/// tenant or workspace. The surrogate <see cref="Id"/> alone never authorizes access: every lookup
/// is scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> so one workspace's edge
/// can never be reached through another workspace's or another tenant's id (threat T5; T1 broken
/// object-level authorization).
///
/// Endpoint linkage: the edge carries <see cref="SourceEntityId"/> and
/// <see cref="TargetEntityId"/>, each a foreign key into <c>entities(id)</c> (the surrogate ids
/// minted in CORE-ENT-002). The two SIMPLE foreign keys guarantee that both endpoints EXIST but
/// NOT that the source, the target and the relationship all live in the SAME workspace — exactly
/// like <see cref="Entity.EntityTypeId"/> does not DB-enforce that the type is in the same
/// workspace, and like <c>ContentBlock.SceneId</c> does not DB-enforce its scene's workspace.
/// Enforcing that both endpoints are in the relationship's own workspace is the responsibility of
/// the future create-relationship application flow (a later endpoint story), which resolves each
/// endpoint through the workspace-scoped <c>IEntityRepository.FindByIdAsync(org, ws, entityId)</c>
/// BEFORE creating the relationship; the aggregate cannot perform that cross-aggregate check
/// itself. This mirrors the established <see cref="Entity"/>/<c>entity_type_id</c> and
/// ContentBlock/<c>scene_id</c> precedents and is a documented carry-over, NOT a silently ignored
/// hole.
///
/// Self-loop invariant: an edge CONNECTS TWO DISTINCT entities, so a self-loop
/// (<see cref="SourceEntityId"/> == <see cref="TargetEntityId"/>) is rejected as an invariant
/// violation. A self-relationship has no meaning in this generic edge model (an entity related to
/// itself), and rejecting it keeps the graph free of degenerate edges; allowing self-loops would
/// be defensible for some graph models, but the distinct-endpoints rule is the safer default and
/// is the one pinned by the tests.
///
/// Identity / uniqueness invariant: a relationship is identified by its surrogate <see cref="Id"/>
/// (UUIDv7). It also has a per-workspace natural key — the directed edge of a given kind:
/// (<c>workspace_id</c>, <c>source_entity_id</c>, <c>target_entity_id</c>, <c>relationship_kind</c>)
/// is unique, so the SAME directed edge of the SAME kind cannot be created twice (a duplicate
/// insert is rejected via <see cref="EntityRelationshipAddResult.Duplicate"/>, mirroring
/// <see cref="EntityType"/>'s per-workspace key). This prevents accidental duplicate edges while
/// still allowing two entities to be connected by DIFFERENT kinds, and allowing the reverse edge
/// (target -&gt; source) as a separate row.
///
/// Immutability: a relationship is essentially immutable — its endpoints, its kind, its tenant and
/// its workspace never change. Redefining an endpoint or a kind would be indistinguishable from
/// creating a different edge, so there is deliberately NO rename/redefine mutator (a vertical that
/// wants a different edge deletes this one and creates a new one). This keeps the aggregate's
/// surface minimal, exactly as ENT-003 requires.
///
/// There is deliberately no relationship attribute payload, no lifecycle status, no visibility
/// surface and no graph-traversal logic here. ENT-003 is the bare edge model: endpoints + kind +
/// timestamps. A relationship attribute-values JSON document is intentionally NOT added — it would
/// be speculative scope with no current consumer (an edge's meaning is fully carried by its kind),
/// so it is deferred until a story needs it. Template loading / schema-conformance (CORE-ENT-004)
/// and entity search / visibility filtering / graph traversal (CORE-ENT-005) are later stories and
/// are deliberately omitted.
/// </summary>
public sealed class EntityRelationship
{
    /// <summary>
    /// Minimum relationship-kind length. A single-character kind is too easy to collide with or
    /// mistype, so the shortest accepted kind is two characters (mirrors
    /// <see cref="EntityType.MinTypeKeyLength"/>).
    /// </summary>
    public const int MinRelationshipKindLength = 2;

    /// <summary>
    /// Maximum relationship-kind length. The kind is a canonical token used in the per-workspace
    /// unique edge index and in list lookups; this bound keeps it well within identifier and index
    /// limits (mirrors <see cref="EntityType.MaxTypeKeyLength"/>).
    /// </summary>
    public const int MaxRelationshipKindLength = 64;

    // Canonical relationship-kind shape: lower-case letters/digits in dash-separated groups (no
    // leading, trailing or doubled dash). Identical to the EntityType.TypeKey / workspace-slug
    // shape, so the edge label behaves consistently, compares cleanly (ordinal, case-sensitive)
    // and is free of control characters or casing ambiguity. Crucially this is a GENERIC SHAPE
    // rule, not a vocabulary rule: it matches any well-formed slug and contains no knowledge of any
    // specific relationship name (the template boundary, docs/04 — the edge is defined by data, not
    // by Core source).
    private static readonly Regex _relationshipKindPattern = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private EntityRelationship(
        Guid id,
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        Guid targetEntityId,
        string relationshipKind,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Entity relationship id must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (sourceEntityId == Guid.Empty)
        {
            throw new ArgumentException("Source entity id must not be empty.", nameof(sourceEntityId));
        }

        if (targetEntityId == Guid.Empty)
        {
            throw new ArgumentException("Target entity id must not be empty.", nameof(targetEntityId));
        }

        // Self-loop invariant: an edge connects two DISTINCT entities (see the type remarks). A
        // relationship whose source and target are the same entity is rejected as degenerate.
        if (sourceEntityId == targetEntityId)
        {
            throw new ArgumentException(
                "A relationship must connect two distinct entities (no self-loop).",
                nameof(targetEntityId));
        }

        if (!IsValidRelationshipKind(relationshipKind))
        {
            throw new ArgumentException(
                "Relationship kind violates the relationship kind invariants.",
                nameof(relationshipKind));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "An entity relationship cannot be updated before it was created.",
                nameof(updatedAt));
        }

        Id = id;
        OrganizationId = organizationId;
        WorkspaceId = workspaceId;
        SourceEntityId = sourceEntityId;
        TargetEntityId = targetEntityId;
        RelationshipKind = relationshipKind;
        // Timestamps are normalized to UTC so the persisted timestamptz values are
        // offset-independent (docs/10_DATABASE_SCHEMA.md).
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Materialization constructor for the persistence layer.</summary>
    private EntityRelationship() => RelationshipKind = null!;

    /// <summary>
    /// Surrogate key of the relationship row (UUID version 7, time-ordered per
    /// docs/10_DATABASE_SCHEMA.md). On its own it never authorizes access: lookups are always
    /// scoped by <see cref="OrganizationId"/> and <see cref="WorkspaceId"/> (threat T5).
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Tenant boundary of the relationship: the id of the organization that owns the workspace this
    /// edge belongs to (the <c>organization_id</c> foreign key to the <c>organizations</c> table).
    /// Immutable; a relationship never crosses to another organization. The organization boundary is
    /// checked before the workspace boundary, so this column lets isolation be enforced at the row
    /// level (threat T5; docs/06_AUTHORIZATION_MATRIX.md authorization principles).
    /// </summary>
    public Guid OrganizationId { get; }

    /// <summary>
    /// Workspace boundary of the relationship: the id of the workspace this edge belongs to (the
    /// <c>workspace_id</c> foreign key to the <c>workspaces</c> table). Immutable; a relationship
    /// never moves to another workspace (threat T5).
    /// </summary>
    public Guid WorkspaceId { get; }

    /// <summary>
    /// Source endpoint of the directed edge: the id of the <see cref="Entity"/> the edge points
    /// FROM (the <c>source_entity_id</c> foreign key to the <c>entities</c> table). Immutable. The
    /// simple foreign key guarantees the source entity EXISTS but not that it is in the
    /// relationship's workspace; the future create-relationship application flow resolves the
    /// endpoint through a workspace-scoped lookup to enforce that coupling (mirrors
    /// <see cref="Entity.EntityTypeId"/>).
    /// </summary>
    public Guid SourceEntityId { get; }

    /// <summary>
    /// Target endpoint of the directed edge: the id of the <see cref="Entity"/> the edge points TO
    /// (the <c>target_entity_id</c> foreign key to the <c>entities</c> table). Immutable, and always
    /// distinct from <see cref="SourceEntityId"/> (no self-loop). The simple foreign key guarantees
    /// the target entity EXISTS but not that it is in the relationship's workspace; that coupling is
    /// the application layer's responsibility (mirrors <see cref="Entity.EntityTypeId"/>).
    /// </summary>
    public Guid TargetEntityId { get; }

    /// <summary>
    /// Generic, canonical kind/label of the edge — the DATA a vertical template supplies to define
    /// what the relationship means; Core stores it verbatim and never branches on its value (the
    /// template boundary, docs/04). Immutable, a lower-case dash-separated slug (the same shape as
    /// <see cref="EntityType.TypeKey"/>); comparisons are exact (ordinal, case-sensitive).
    /// </summary>
    public string RelationshipKind { get; }

    /// <summary>When this relationship was first created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When this relationship was last updated (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// Creates a new directed relationship in the given workspace (owned by the given organization)
    /// from the source entity to the target entity, of the given kind. The relationship kind is
    /// canonicalized to its lower-case slug form once here so the persisted value and every later
    /// comparison stay consistent; an already-canonical kind is unchanged. The caller (a later
    /// create-relationship endpoint) supplies the tenant, workspace and endpoint ids and the kind
    /// entirely as DATA — Core never inspects the kind's vocabulary (the template boundary,
    /// docs/04). The caller is responsible for having resolved both endpoints through a
    /// workspace-scoped lookup so they are in the relationship's own workspace; the aggregate
    /// enforces only the structural invariants (non-empty distinct ids, a valid kind).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An id is empty, the source and target are the same entity (self-loop), or the relationship
    /// kind violates an invariant.
    /// </exception>
    public static EntityRelationship Create(
        Guid organizationId,
        Guid workspaceId,
        Guid sourceEntityId,
        Guid targetEntityId,
        string relationshipKind,
        DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            organizationId,
            workspaceId,
            sourceEntityId,
            targetEntityId,
            CanonicalizeRelationshipKind(relationshipKind),
            createdAt,
            createdAt);

    /// <summary>
    /// Whether this relationship belongs to exactly the given organization. Empty ids match
    /// nothing. This is the tenant-boundary check, checked before the workspace boundary: a
    /// relationship belongs only to the organization it names, never to any other (threat T5).
    /// </summary>
    public bool BelongsToOrganization(Guid organizationId)
        => organizationId != Guid.Empty && OrganizationId == organizationId;

    /// <summary>
    /// Whether this relationship belongs to exactly the given workspace. Empty ids match nothing.
    /// This is the workspace-boundary check: a relationship belongs only to the workspace it names,
    /// never to any other (threat T5).
    /// </summary>
    public bool BelongsToWorkspace(Guid workspaceId)
        => workspaceId != Guid.Empty && WorkspaceId == workspaceId;

    /// <summary>
    /// Whether this directed edge connects exactly the given source to the given target. Both ids
    /// must match in the SAME direction; empty ids match nothing. Because the edge is directed,
    /// <c>Connects(a, b)</c> and <c>Connects(b, a)</c> are different questions: only the stored
    /// direction returns <see langword="true"/>.
    /// </summary>
    public bool Connects(Guid sourceEntityId, Guid targetEntityId)
        => sourceEntityId != Guid.Empty
            && targetEntityId != Guid.Empty
            && SourceEntityId == sourceEntityId
            && TargetEntityId == targetEntityId;

    /// <summary>
    /// Whether this relationship TOUCHES the given entity as either its source or its target. Empty
    /// ids match nothing. This is the undirected "is this entity an endpoint of the edge" check used
    /// by the by-entity listing; the direction is not considered.
    /// </summary>
    public bool Touches(Guid entityId)
        => entityId != Guid.Empty
            && (SourceEntityId == entityId || TargetEntityId == entityId);

    /// <summary>
    /// Whether this relationship is the one identified by the given id WITHIN the given organization
    /// and workspace. All three must match; an empty id matches nothing. A relationship with the
    /// same id under a different tenant or workspace (which cannot happen for a single row but guards
    /// the caller's intent) returns <see langword="false"/>: the surrogate id is only ever honored
    /// inside its tenant and workspace (threat T1/T5).
    /// </summary>
    public bool Identifies(Guid organizationId, Guid workspaceId, Guid id)
        => BelongsToOrganization(organizationId)
            && BelongsToWorkspace(workspaceId)
            && id != Guid.Empty
            && Id == id;

    /// <summary>
    /// Whether the given value is a valid canonical relationship kind: lower-case, dash-separated
    /// alphanumeric groups, within the length bounds and free of control characters. Validation does
    /// not mutate; callers pass an already-canonical value or use
    /// <see cref="CanonicalizeRelationshipKind"/> first. This is a generic SHAPE check with no
    /// knowledge of any specific relationship vocabulary (the template boundary, docs/04).
    /// </summary>
    public static bool IsValidRelationshipKind(string? value)
        => !string.IsNullOrEmpty(value)
            && value.Length >= MinRelationshipKindLength
            && value.Length <= MaxRelationshipKindLength
            && _relationshipKindPattern.IsMatch(value);

    /// <summary>
    /// Canonicalizes a relationship kind to its stored form (trimmed and lower-cased using the
    /// invariant culture) and validates it. Canonicalization is purely casing/whitespace: it never
    /// rewrites characters, so an input that is not already a valid slug shape is rejected rather
    /// than coerced.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a valid relationship kind after canonicalization.
    /// </exception>
    public static string CanonicalizeRelationshipKind(string? value)
    {
        var canonical = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsValidRelationshipKind(canonical))
        {
            throw new ArgumentException(
                "Relationship kind violates the relationship kind invariants.",
                nameof(value));
        }

        return canonical;
    }

    /// <summary>
    /// Identifier-only representation that is safe for structured logs: relationship id,
    /// organization id, workspace id, source entity id, target entity id and relationship kind. No
    /// free-form content is exposed — the relationship kind is a canonical slug identifier (not
    /// free-form content), so it is safe to log, exactly like <see cref="EntityType.TypeKey"/>
    /// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md: logs carry identifiers, not free-form
    /// content).
    /// </summary>
    public override string ToString()
        => $"EntityRelationship {Id} org={OrganizationId} ws={WorkspaceId} "
            + $"source={SourceEntityId} target={TargetEntityId} kind={RelationshipKind}";
}
