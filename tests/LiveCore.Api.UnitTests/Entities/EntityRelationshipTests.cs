// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for the <see cref="EntityRelationship"/> invariants, its tenant/workspace boundary,
/// its directed-edge endpoints, its relationship-kind TEMPLATE VALIDATION (canonical slug shape +
/// bounds), the self-loop rule, its endpoint/identity guards and its log safety (CORE-ENT-003): an
/// entity relationship is a generic, DATA-DRIVEN GRAPH EDGE between two entities
/// (csv/database_tables.csv: "Graph edges"). It is workspace-scoped and tenant-scoped and belongs
/// only to the workspace and organization it names; it connects two DISTINCT entities in a
/// direction; its kind is opaque data the model never branches on; and ToString stays log-safe
/// (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every example relationship kind here is
/// GENERIC and NEUTRAL ("links-to", "relates-to", "rel-alpha") — no vertical vocabulary appears
/// anywhere, and the model treats the kind purely as data (it never branches on it), proving the
/// relationship is generic and template-driven (AGENTS.md, csv/forbidden_core_terms.csv).
///
/// "Visibility tests": entity/relationship visibility FILTERING is CORE-ENT-005 — the bare edge
/// model has no visibility surface — so the applicable validation here is the template/structural
/// validation plus the tenant/workspace isolation covered by these tests and the repository tests.
/// </summary>
public class EntityRelationshipTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _sourceEntityId = Guid.CreateVersion7();
    private static readonly Guid _targetEntityId = Guid.CreateVersion7();
    private static readonly Guid _otherEntityId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _kind = "links-to";

    private static EntityRelationship CreateRelationship(string kind = _kind)
        => EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, kind, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_sets_the_metadata()
    {
        var relationship = EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, "relates-to", _createdAt);

        Assert.NotEqual(Guid.Empty, relationship.Id);
        Assert.Equal(_organizationId, relationship.OrganizationId);
        Assert.Equal(_workspaceId, relationship.WorkspaceId);
        Assert.Equal(_sourceEntityId, relationship.SourceEntityId);
        Assert.Equal(_targetEntityId, relationship.TargetEntityId);
        Assert.Equal("relates-to", relationship.RelationshipKind);
        Assert.Equal(_createdAt, relationship.CreatedAt);
        Assert.Equal(_createdAt, relationship.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreateRelationship();
        var second = CreateRelationship();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_canonicalizes_the_relationship_kind()
    {
        // The kind is canonicalized to its lower-case slug form once at creation (re-cased/padded
        // input still resolves to the stored canonical value), mirroring EntityType.TypeKey.
        var relationship = EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, "  RELATES-TO  ", _createdAt);

        Assert.Equal("relates-to", relationship.RelationshipKind);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var relationship = EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, _kind, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, relationship.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), relationship.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            Guid.Empty, _workspaceId, _sourceEntityId, _targetEntityId, _kind, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, Guid.Empty, _sourceEntityId, _targetEntityId, _kind, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_source_entity_id()
        => Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, _workspaceId, Guid.Empty, _targetEntityId, _kind, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_target_entity_id()
        => Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, Guid.Empty, _kind, _createdAt));

    // --- Self-loop rule (reject distinct-endpoints violation) ------------------

    [Fact]
    public void Create_rejects_a_self_loop()
    {
        // A relationship connects two DISTINCT entities: an edge whose source and target are the same
        // entity is rejected as degenerate (the chosen, pinned behavior; see the type remarks).
        Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _sourceEntityId, _kind, _createdAt));
    }

    // --- Relationship-kind TEMPLATE VALIDATION (canonical slug shape + bounds) --

    [Theory]
    [InlineData("ab")]
    [InlineData("links-to")]
    [InlineData("relates-to")]
    [InlineData("rel-alpha")]
    [InlineData("a1-b2-c3")]
    public void Create_accepts_well_formed_relationship_kinds(string kind)
    {
        // Template validation: any well-formed canonical slug is accepted as the relationship kind.
        // Only the generic SHAPE is checked here, never the kind's vocabulary (the template boundary,
        // docs/04).
        var relationship = EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, kind, _createdAt);

        Assert.Equal(kind, relationship.RelationshipKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a")] // too short
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("Has Space")]
    [InlineData("under_score")]
    [InlineData("bad!char")]
    [InlineData("bad\tcontrol")]
    public void Create_rejects_malformed_relationship_kinds(string? kind)
        => Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, kind!, _createdAt));

    [Fact]
    public void Create_rejects_a_relationship_kind_over_the_length_bound()
    {
        var tooLong = new string('a', EntityRelationship.MaxRelationshipKindLength + 1);

        Assert.Throws<ArgumentException>(() => EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, tooLong, _createdAt));
    }

    [Fact]
    public void Create_accepts_a_relationship_kind_at_the_length_bound()
    {
        var atBound = new string('a', EntityRelationship.MaxRelationshipKindLength);

        var relationship = EntityRelationship.Create(
            _organizationId, _workspaceId, _sourceEntityId, _targetEntityId, atBound, _createdAt);

        Assert.Equal(atBound, relationship.RelationshipKind);
    }

    [Fact]
    public void IsValidRelationshipKind_accepts_canonical_slugs_and_rejects_others()
    {
        Assert.True(EntityRelationship.IsValidRelationshipKind("links-to"));
        Assert.True(EntityRelationship.IsValidRelationshipKind(
            new string('a', EntityRelationship.MaxRelationshipKindLength)));
        Assert.False(EntityRelationship.IsValidRelationshipKind(""));
        Assert.False(EntityRelationship.IsValidRelationshipKind(null));
        Assert.False(EntityRelationship.IsValidRelationshipKind("a")); // too short
        Assert.False(EntityRelationship.IsValidRelationshipKind("UPPER"));
        Assert.False(EntityRelationship.IsValidRelationshipKind("-leading"));
        Assert.False(EntityRelationship.IsValidRelationshipKind(
            new string('a', EntityRelationship.MaxRelationshipKindLength + 1)));
    }

    [Fact]
    public void CanonicalizeRelationshipKind_lowercases_and_trims_and_rejects_malformed()
    {
        Assert.Equal("links-to", EntityRelationship.CanonicalizeRelationshipKind("  LINKS-TO  "));
        // Canonicalization is purely casing/whitespace; a non-slug shape is rejected, not coerced.
        Assert.Throws<ArgumentException>(() => EntityRelationship.CanonicalizeRelationshipKind("bad key!"));
    }

    [Fact]
    public void Constructing_with_an_updated_before_created_is_rejected()
    {
        // The aggregate enforces updatedAt >= createdAt: a relationship cannot be updated before it
        // was created. The Create factory always passes createdAt == updatedAt, so the invariant is
        // reached through the private materialization constructor (the path a corrupt/hand-built row
        // would take). The guard throws an ArgumentException, surfaced through reflection as the inner
        // exception.
        var constructor = typeof(EntityRelationship).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid),
                typeof(string), typeof(DateTimeOffset), typeof(DateTimeOffset),
            },
            modifiers: null);
        Assert.NotNull(constructor);

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            constructor.Invoke(new object[]
            {
                Guid.CreateVersion7(),
                _organizationId,
                _workspaceId,
                _sourceEntityId,
                _targetEntityId,
                _kind,
                // createdAt is _updatedAt (later); updatedAt is _createdAt (earlier).
                _updatedAt,
                _createdAt,
            }));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    // --- Tenant / workspace boundary -------------------------------------------

    [Fact]
    public void Relationship_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a relationship belongs only to the organization it names.
        // The organization boundary is checked before the workspace boundary.
        var relationship = CreateRelationship();

        Assert.True(relationship.BelongsToOrganization(_organizationId));
        Assert.False(relationship.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(relationship.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Relationship_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): an edge in workspace W1 is not in workspace W2.
        var relationship = CreateRelationship();

        Assert.True(relationship.BelongsToWorkspace(_workspaceId));
        Assert.False(relationship.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(relationship.BelongsToWorkspace(Guid.Empty));
    }

    // --- Directed-edge endpoints (Connects / Touches) --------------------------

    [Fact]
    public void Connects_matches_only_the_stored_direction()
    {
        // The edge is directed: Connects(source, target) is true, but Connects(target, source) — the
        // reverse direction — is false.
        var relationship = CreateRelationship();

        Assert.True(relationship.Connects(_sourceEntityId, _targetEntityId));
        Assert.False(relationship.Connects(_targetEntityId, _sourceEntityId));
        Assert.False(relationship.Connects(_sourceEntityId, _otherEntityId));
        Assert.False(relationship.Connects(Guid.Empty, _targetEntityId));
        Assert.False(relationship.Connects(_sourceEntityId, Guid.Empty));
    }

    [Fact]
    public void Touches_matches_either_endpoint_regardless_of_direction()
    {
        // The undirected "is this entity an endpoint" check: true for the source and the target,
        // false for a third entity and for an empty id.
        var relationship = CreateRelationship();

        Assert.True(relationship.Touches(_sourceEntityId));
        Assert.True(relationship.Touches(_targetEntityId));
        Assert.False(relationship.Touches(_otherEntityId));
        Assert.False(relationship.Touches(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A relationship is the one identified by its id only WITHIN its tenant and workspace: the
        // surrogate id is never honored across a foreign tenant or workspace (threat T1/T5).
        var relationship = CreateRelationship();

        Assert.True(relationship.Identifies(_organizationId, _workspaceId, relationship.Id));
        Assert.False(relationship.Identifies(_foreignOrganizationId, _workspaceId, relationship.Id));
        Assert.False(relationship.Identifies(_organizationId, _foreignWorkspaceId, relationship.Id));
        Assert.False(relationship.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(relationship.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_and_the_kind_but_no_free_form_content()
    {
        // Log-safety (threat T7): structured logs carry identifiers (relationship id, org id,
        // workspace id, source/target ids) and the canonical kind slug — never any free-form content
        // (the edge has none). The kind is a canonical slug identifier, safe to log like
        // EntityType.TypeKey.
        var relationship = CreateRelationship("rel-alpha");

        var text = relationship.ToString();

        Assert.Contains(relationship.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_sourceEntityId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_targetEntityId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("rel-alpha", text, StringComparison.Ordinal);
    }
}
