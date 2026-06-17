// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for the <see cref="Entity"/> invariants, its tenant/workspace boundary, its type
/// linkage, its attribute-values TEMPLATE VALIDATION (JSON well-formedness + size), its
/// reject-with-no-mutation mutators and its log safety (CORE-ENT-002): an entity is the generic,
/// DATA-DRIVEN "Generic domain object" (docs/03_DOMAIN_LANGUAGE.md) — one INSTANCE of an entity
/// type. It is workspace-scoped and tenant-scoped and belongs only to the workspace and
/// organization it names; it is an instance of exactly one entity type; redefining its values or
/// renaming it never moves it; and ToString stays log-safe (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every example name/value here is GENERIC
/// and NEUTRAL ("alpha", "sample entity", attribute JSON like {"label":"value"}) — no vertical
/// vocabulary appears anywhere, and the model treats the name and values purely as data (it never
/// branches on them), proving the entity is generic and template-driven (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class EntityTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();
    private static readonly Guid _entityTypeId = Guid.CreateVersion7();
    private static readonly Guid _foreignEntityTypeId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _validValues = """{"label":"value"}""";

    private static Entity CreateEntity(string name = "alpha")
        => Entity.Create(_organizationId, _workspaceId, _entityTypeId, name, _validValues, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_sets_the_metadata()
    {
        var entity = Entity.Create(
            _organizationId, _workspaceId, _entityTypeId, "sample entity", _validValues, _createdAt);

        Assert.NotEqual(Guid.Empty, entity.Id);
        Assert.Equal(_organizationId, entity.OrganizationId);
        Assert.Equal(_workspaceId, entity.WorkspaceId);
        Assert.Equal(_entityTypeId, entity.EntityTypeId);
        Assert.Equal("sample entity", entity.Name);
        Assert.Equal(_validValues, entity.AttributeValues);
        Assert.Equal(_createdAt, entity.CreatedAt);
        Assert.Equal(_createdAt, entity.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreateEntity();
        var second = CreateEntity();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_trims_the_name_and_values()
    {
        var entity = Entity.Create(
            _organizationId, _workspaceId, _entityTypeId, "  Spaced Name  ", "  " + _validValues + "  ", _createdAt);

        Assert.Equal("Spaced Name", entity.Name);
        Assert.Equal(_validValues, entity.AttributeValues);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var entity = Entity.Create(
            _organizationId, _workspaceId, _entityTypeId, "alpha", _validValues, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, entity.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), entity.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(Guid.Empty, _workspaceId, _entityTypeId, "alpha", _validValues, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, Guid.Empty, _entityTypeId, "alpha", _validValues, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_entity_type_id()
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, Guid.Empty, "alpha", _validValues, _createdAt));

    // --- Name invariants -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_name(string? name)
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, _entityTypeId, name!, _validValues, _createdAt));

    [Fact]
    public void Create_rejects_a_name_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, _entityTypeId, "bad\tname", _validValues, _createdAt));

    [Fact]
    public void Create_rejects_a_name_over_the_length_bound()
    {
        var tooLong = new string('a', Entity.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, _entityTypeId, tooLong, _validValues, _createdAt));
    }

    [Fact]
    public void IsValidName_accepts_bounded_non_control_names_and_rejects_others()
    {
        Assert.True(Entity.IsValidName("alpha"));
        Assert.True(Entity.IsValidName(new string('a', Entity.MaxNameLength)));
        Assert.False(Entity.IsValidName(""));
        Assert.False(Entity.IsValidName("   "));
        Assert.False(Entity.IsValidName(null));
        Assert.False(Entity.IsValidName("bad\tname"));
        Assert.False(Entity.IsValidName(new string('a', Entity.MaxNameLength + 1)));
    }

    // --- Attribute-values TEMPLATE VALIDATION (well-formed JSON + bounded) ------

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"a":1,"b":[true,null,"x"]}""")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a scalar string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Create_accepts_well_formed_json_values(string values)
    {
        // Template validation: any well-formed JSON document is accepted as attribute values. Only
        // well-formedness is checked here, never the values against the entity type's attribute
        // schema (schema-conformance is the template engine / CORE-ENT-004).
        var entity = Entity.Create(
            _organizationId, _workspaceId, _entityTypeId, "alpha", values, _createdAt);

        Assert.Equal(values, entity.AttributeValues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{")] // unterminated object
    [InlineData("{\"a\":}")] // missing value
    [InlineData("not json")]
    [InlineData("{\"a\":1,}")] // trailing comma
    public void Create_rejects_malformed_or_blank_values(string? values)
        => Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, _entityTypeId, "alpha", values!, _createdAt));

    [Fact]
    public void Create_rejects_values_over_the_size_bound()
    {
        // A well-formed but oversized JSON document is rejected by the size bound.
        var oversizedInner = new string('a', Entity.MaxAttributeValuesLength);
        var oversized = "\"" + oversizedInner + "\""; // valid JSON string, but over the bound

        Assert.Throws<ArgumentException>(
            () => Entity.Create(_organizationId, _workspaceId, _entityTypeId, "alpha", oversized, _createdAt));
    }

    [Fact]
    public void Create_accepts_values_at_the_size_bound()
    {
        // A well-formed JSON document whose total length is exactly the bound is accepted
        // (inclusive bound).
        var atBound = "\"" + new string('a', Entity.MaxAttributeValuesLength - 2) + "\"";
        Assert.Equal(Entity.MaxAttributeValuesLength, atBound.Length);

        var entity = Entity.Create(
            _organizationId, _workspaceId, _entityTypeId, "alpha", atBound, _createdAt);

        Assert.Equal(atBound, entity.AttributeValues);
    }

    [Fact]
    public void IsValidAttributeValues_matches_the_validator()
    {
        Assert.True(Entity.IsValidAttributeValues("{}"));
        Assert.False(Entity.IsValidAttributeValues("{"));
        Assert.False(Entity.IsValidAttributeValues(null));
    }

    [Fact]
    public void Constructing_with_an_updated_before_created_is_rejected()
    {
        // The aggregate enforces updatedAt >= createdAt: an entity cannot be updated before it was
        // created. The Create factory always passes createdAt == updatedAt, so the invariant is
        // reached through the private materialization constructor (the path a corrupt/hand-built
        // row would take). The guard throws an ArgumentException, surfaced through reflection as the
        // inner exception.
        var constructor = typeof(Entity).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid),
                typeof(string), typeof(string), typeof(DateTimeOffset), typeof(DateTimeOffset),
            },
            modifiers: null);
        Assert.NotNull(constructor);

        var invocation = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            constructor.Invoke(new object[]
            {
                Guid.CreateVersion7(),
                _organizationId,
                _workspaceId,
                _entityTypeId,
                "alpha",
                _validValues,
                // createdAt is _updatedAt (later); updatedAt is _createdAt (earlier).
                _updatedAt,
                _createdAt,
            }));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    // --- Tenant / workspace / type boundary ------------------------------------

    [Fact]
    public void Entity_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): an entity belongs only to the organization it names.
        // The organization boundary is checked before the workspace boundary.
        var entity = CreateEntity();

        Assert.True(entity.BelongsToOrganization(_organizationId));
        Assert.False(entity.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(entity.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void Entity_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): an entity in workspace W1 is not in workspace W2.
        var entity = CreateEntity();

        Assert.True(entity.BelongsToWorkspace(_workspaceId));
        Assert.False(entity.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(entity.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Entity_is_an_instance_only_of_its_own_type()
    {
        // Negative type case: an entity is an instance only of the type it names; a different type
        // id (and an empty id) never matches.
        var entity = CreateEntity();

        Assert.True(entity.IsOfType(_entityTypeId));
        Assert.False(entity.IsOfType(_foreignEntityTypeId));
        Assert.False(entity.IsOfType(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // An entity is the one identified by its id only WITHIN its tenant and workspace: the
        // surrogate id is never honored across a foreign tenant or workspace (threat T1/T5).
        var entity = CreateEntity();

        Assert.True(entity.Identifies(_organizationId, _workspaceId, entity.Id));
        Assert.False(entity.Identifies(_foreignOrganizationId, _workspaceId, entity.Id));
        Assert.False(entity.Identifies(_organizationId, _foreignWorkspaceId, entity.Id));
        Assert.False(entity.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(entity.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    // --- Rename (reject-with-no-mutation) --------------------------------------

    [Fact]
    public void Rename_changes_only_the_name_and_timestamp()
    {
        var entity = CreateEntity();

        entity.Rename("New Name", _updatedAt);

        Assert.Equal("New Name", entity.Name);
        Assert.Equal(_updatedAt, entity.UpdatedAt);
        Assert.Equal(_createdAt, entity.CreatedAt);
        // The tenant boundary, the workspace, the id, the type and the values are immutable across a
        // rename (threat T5): renaming never moves the entity or changes its type linkage.
        Assert.Equal(_organizationId, entity.OrganizationId);
        Assert.Equal(_workspaceId, entity.WorkspaceId);
        Assert.Equal(_entityTypeId, entity.EntityTypeId);
        Assert.Equal(_validValues, entity.AttributeValues);
    }

    [Fact]
    public void Rename_trims_the_name()
    {
        var entity = CreateEntity();

        entity.Rename("  Spaced  ", _updatedAt);

        Assert.Equal("Spaced", entity.Name);
    }

    [Fact]
    public void Rename_rejects_an_invalid_name_and_leaves_the_entity_untouched()
    {
        var entity = CreateEntity();

        Assert.Throws<ArgumentException>(() => entity.Rename("   ", _updatedAt));

        // The rejected rename did not change the name or re-stamp the update timestamp.
        Assert.Equal("alpha", entity.Name);
        Assert.Equal(_createdAt, entity.UpdatedAt);
    }

    // --- RedefineAttributeValues (reject-with-no-mutation) ----------------------

    [Fact]
    public void RedefineAttributeValues_replaces_the_values_and_timestamp_only()
    {
        var entity = CreateEntity();
        const string newValues = """{"count":3}""";

        entity.RedefineAttributeValues(newValues, _updatedAt);

        Assert.Equal(newValues, entity.AttributeValues);
        Assert.Equal(_updatedAt, entity.UpdatedAt);
        // The identity, tenant, workspace, type and name are immutable here (threat T5).
        Assert.Equal(_organizationId, entity.OrganizationId);
        Assert.Equal(_workspaceId, entity.WorkspaceId);
        Assert.Equal(_entityTypeId, entity.EntityTypeId);
        Assert.Equal("alpha", entity.Name);
    }

    [Fact]
    public void RedefineAttributeValues_trims_the_values()
    {
        var entity = CreateEntity();
        const string newValues = """{"x":1}""";

        entity.RedefineAttributeValues("  " + newValues + "  ", _updatedAt);

        Assert.Equal(newValues, entity.AttributeValues);
    }

    [Fact]
    public void RedefineAttributeValues_rejects_malformed_values_and_leaves_the_entity_untouched()
    {
        var entity = CreateEntity();

        Assert.Throws<ArgumentException>(() => entity.RedefineAttributeValues("{ not json", _updatedAt));

        // The rejected redefine left the prior values and update timestamp intact.
        Assert.Equal(_validValues, entity.AttributeValues);
        Assert.Equal(_createdAt, entity.UpdatedAt);
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_but_no_name_or_values()
    {
        // Log-safety (threat T7): structured logs carry identifiers (entity id, org id, workspace
        // id, type id), never the name or the attribute-values content (template-/host-supplied
        // data).
        var entity = Entity.Create(
            _organizationId,
            _workspaceId,
            _entityTypeId,
            "Secret Name",
            """{"secret":"do-not-log-this"}""",
            _createdAt);

        var text = entity.ToString();

        Assert.Contains(entity.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_entityTypeId.ToString(), text, StringComparison.Ordinal);
        // Neither the name nor the values content is written to logs.
        Assert.DoesNotContain("Secret Name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this", text, StringComparison.Ordinal);
    }
}
