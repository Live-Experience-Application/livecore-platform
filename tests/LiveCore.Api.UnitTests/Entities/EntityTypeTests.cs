using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for the <see cref="EntityType"/> invariants, its tenant/workspace boundary, its
/// canonical type key, its attribute-schema TEMPLATE VALIDATION (JSON well-formedness + size),
/// its reject-with-no-mutation mutators and its log safety (CORE-ENT-001): an entity type is the
/// generic, DATA-DRIVEN "Template-defined type for an entity" (docs/03_DOMAIN_LANGUAGE.md). It is
/// workspace-scoped and tenant-scoped and belongs only to the workspace and organization it
/// names; its key is unique per workspace; redefining its schema or renaming it never moves it;
/// and ToString stays log-safe (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// THE TEMPLATE BOUNDARY (docs/04_PRODUCT_BOUNDARIES.md): every example key/name here is GENERIC
/// and NEUTRAL ("type-alpha", "type-beta", "profile", "note", "record") — no vertical vocabulary
/// appears anywhere, and the model treats the key purely as data (it never branches on its value),
/// proving the type is generic and template-driven (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class EntityTypeTests
{
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _foreignOrganizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _foreignWorkspaceId = Guid.CreateVersion7();

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _validSchema = """{"fields":[{"name":"label","kind":"text"}]}""";

    private static EntityType CreateEntityType(string typeKey = "type-alpha")
        => EntityType.Create(_organizationId, _workspaceId, typeKey, "Type Alpha", _validSchema, _createdAt);

    // --- Creation invariants ---------------------------------------------------

    [Fact]
    public void Create_sets_the_metadata()
    {
        var entityType = EntityType.Create(
            _organizationId, _workspaceId, "profile", "Profile", _validSchema, _createdAt);

        Assert.NotEqual(Guid.Empty, entityType.Id);
        Assert.Equal(_organizationId, entityType.OrganizationId);
        Assert.Equal(_workspaceId, entityType.WorkspaceId);
        Assert.Equal("profile", entityType.TypeKey);
        Assert.Equal("Profile", entityType.DisplayName);
        Assert.Equal(_validSchema, entityType.AttributeSchema);
        Assert.Equal(_createdAt, entityType.CreatedAt);
        Assert.Equal(_createdAt, entityType.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = CreateEntityType();
        var second = CreateEntityType();

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_canonicalizes_the_type_key_to_lower_case_and_trims()
    {
        // The key is a canonical slug: casing and surrounding whitespace are normalized once at
        // creation so a re-cased key can never silently address a different type.
        var entityType = EntityType.Create(
            _organizationId, _workspaceId, "  Type-Beta  ", "Type Beta", _validSchema, _createdAt);

        Assert.Equal("type-beta", entityType.TypeKey);
    }

    [Fact]
    public void Create_trims_the_display_name_and_schema()
    {
        var entityType = EntityType.Create(
            _organizationId, _workspaceId, "note", "  Spaced Name  ", "  " + _validSchema + "  ", _createdAt);

        Assert.Equal("Spaced Name", entityType.DisplayName);
        Assert.Equal(_validSchema, entityType.AttributeSchema);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.FromHours(2));

        var entityType = EntityType.Create(
            _organizationId, _workspaceId, "record", "Record", _validSchema, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, entityType.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), entityType.CreatedAt);
    }

    [Fact]
    public void Create_rejects_an_empty_organization_id()
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(Guid.Empty, _workspaceId, "type-alpha", "Type Alpha", _validSchema, _createdAt));

    [Fact]
    public void Create_rejects_an_empty_workspace_id()
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, Guid.Empty, "type-alpha", "Type Alpha", _validSchema, _createdAt));

    // --- Type key invariants ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("a")] // below the minimum length
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("Has Space")]
    [InlineData("under_score")]
    [InlineData("punc!")]
    [InlineData("bad\tkey")] // control character
    public void Create_rejects_an_invalid_type_key(string? typeKey)
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, typeKey!, "Name", _validSchema, _createdAt));

    [Fact]
    public void Create_rejects_a_type_key_over_the_length_bound()
    {
        var tooLong = new string('a', EntityType.MaxTypeKeyLength + 1);

        Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, tooLong, "Name", _validSchema, _createdAt));
    }

    [Fact]
    public void IsValidTypeKey_accepts_canonical_slugs_and_rejects_malformed()
    {
        Assert.True(EntityType.IsValidTypeKey("type-alpha"));
        Assert.True(EntityType.IsValidTypeKey("profile"));
        Assert.True(EntityType.IsValidTypeKey("a1-b2-c3"));
        Assert.False(EntityType.IsValidTypeKey("Type-Alpha")); // upper case is not canonical
        Assert.False(EntityType.IsValidTypeKey("a"));
        Assert.False(EntityType.IsValidTypeKey("-x"));
        Assert.False(EntityType.IsValidTypeKey(null));
    }

    [Fact]
    public void CanonicalizeTypeKey_lower_cases_and_trims_but_rejects_unfixable_input()
    {
        Assert.Equal("type-alpha", EntityType.CanonicalizeTypeKey("  TYPE-ALPHA  "));
        // Canonicalization is purely casing/whitespace; a bad shape is rejected, not coerced.
        Assert.Throws<ArgumentException>(() => EntityType.CanonicalizeTypeKey("bad key!"));
    }

    // --- Display name invariants -----------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_display_name(string? displayName)
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, "type-alpha", displayName!, _validSchema, _createdAt));

    [Fact]
    public void Create_rejects_a_display_name_with_control_characters()
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, "type-alpha", "bad\tname", _validSchema, _createdAt));

    [Fact]
    public void Create_rejects_a_display_name_over_the_length_bound()
    {
        var tooLong = new string('a', EntityType.MaxDisplayNameLength + 1);

        Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, "type-alpha", tooLong, _validSchema, _createdAt));
    }

    // --- Attribute-schema TEMPLATE VALIDATION (well-formed JSON + bounded) ------

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"a":1,"b":[true,null,"x"]}""")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a scalar string\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Create_accepts_well_formed_json_schemas(string schema)
    {
        // Template validation: any well-formed JSON document is accepted as an attribute schema.
        // Only well-formedness is checked here, never the schema against a template (CORE-ENT-004).
        var entityType = EntityType.Create(
            _organizationId, _workspaceId, "type-alpha", "Type Alpha", schema, _createdAt);

        Assert.Equal(schema, entityType.AttributeSchema);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{")] // unterminated object
    [InlineData("{\"a\":}")] // missing value
    [InlineData("not json")]
    [InlineData("{\"a\":1,}")] // trailing comma
    public void Create_rejects_a_malformed_or_blank_schema(string? schema)
        => Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, "type-alpha", "Name", schema!, _createdAt));

    [Fact]
    public void Create_rejects_a_schema_over_the_size_bound()
    {
        // A well-formed but oversized JSON document is rejected by the size bound.
        var oversizedInner = new string('a', EntityType.MaxAttributeSchemaLength);
        var oversized = "\"" + oversizedInner + "\""; // valid JSON string, but over the bound

        Assert.Throws<ArgumentException>(
            () => EntityType.Create(_organizationId, _workspaceId, "type-alpha", "Name", oversized, _createdAt));
    }

    [Fact]
    public void IsValidAttributeSchema_matches_the_validator()
    {
        Assert.True(EntityType.IsValidAttributeSchema("{}"));
        Assert.False(EntityType.IsValidAttributeSchema("{"));
        Assert.False(EntityType.IsValidAttributeSchema(null));
    }

    [Fact]
    public void Constructing_with_an_updated_before_created_is_rejected()
    {
        // The aggregate enforces updatedAt >= createdAt: a type cannot be updated before it was
        // created. The Create factory always passes createdAt == updatedAt, so the invariant is
        // reached through the private materialization constructor (the path a corrupt/hand-built
        // row would take). The guard throws an ArgumentException, surfaced through reflection as
        // the inner exception.
        var constructor = typeof(EntityType).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(Guid), typeof(Guid), typeof(Guid), typeof(string),
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
                "type-alpha",
                "Type Alpha",
                _validSchema,
                // createdAt is _updatedAt (later); updatedAt is _createdAt (earlier).
                _updatedAt,
                _createdAt,
            }));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    // --- Tenant / workspace boundary -------------------------------------------

    [Fact]
    public void EntityType_belongs_only_to_its_own_organization()
    {
        // Negative tenant case (threat T5): a type belongs only to the organization it names.
        // The organization boundary is checked before the workspace boundary.
        var entityType = CreateEntityType();

        Assert.True(entityType.BelongsToOrganization(_organizationId));
        Assert.False(entityType.BelongsToOrganization(_foreignOrganizationId));
        Assert.False(entityType.BelongsToOrganization(Guid.Empty));
    }

    [Fact]
    public void EntityType_belongs_only_to_its_own_workspace()
    {
        // Negative workspace case (threat T5): a type in workspace W1 is not in workspace W2.
        var entityType = CreateEntityType();

        Assert.True(entityType.BelongsToWorkspace(_workspaceId));
        Assert.False(entityType.BelongsToWorkspace(_foreignWorkspaceId));
        Assert.False(entityType.BelongsToWorkspace(Guid.Empty));
    }

    [Fact]
    public void Identifies_requires_the_exact_organization_workspace_and_id()
    {
        // A type is the one identified by its id only WITHIN its tenant and workspace: the
        // surrogate id is never honored across a foreign tenant or workspace (threat T1/T5).
        var entityType = CreateEntityType();

        Assert.True(entityType.Identifies(_organizationId, _workspaceId, entityType.Id));
        Assert.False(entityType.Identifies(_foreignOrganizationId, _workspaceId, entityType.Id));
        Assert.False(entityType.Identifies(_organizationId, _foreignWorkspaceId, entityType.Id));
        Assert.False(entityType.Identifies(_organizationId, _workspaceId, Guid.CreateVersion7()));
        Assert.False(entityType.Identifies(_organizationId, _workspaceId, Guid.Empty));
    }

    [Fact]
    public void HasKeyIn_matches_only_the_exact_key_within_the_exact_tenant_and_workspace()
    {
        var entityType = CreateEntityType("type-alpha");

        Assert.True(entityType.HasKeyIn(_organizationId, _workspaceId, "type-alpha"));
        // Same key in a foreign workspace or tenant is a different type (threat T5).
        Assert.False(entityType.HasKeyIn(_organizationId, _foreignWorkspaceId, "type-alpha"));
        Assert.False(entityType.HasKeyIn(_foreignOrganizationId, _workspaceId, "type-alpha"));
        // A different key never matches; a blank key matches nothing.
        Assert.False(entityType.HasKeyIn(_organizationId, _workspaceId, "type-beta"));
        Assert.False(entityType.HasKeyIn(_organizationId, _workspaceId, "  "));
    }

    // --- Rename (reject-with-no-mutation) --------------------------------------

    [Fact]
    public void Rename_changes_only_the_display_name_and_timestamp()
    {
        var entityType = CreateEntityType();

        entityType.Rename("New Name", _updatedAt);

        Assert.Equal("New Name", entityType.DisplayName);
        Assert.Equal(_updatedAt, entityType.UpdatedAt);
        Assert.Equal(_createdAt, entityType.CreatedAt);
        // The tenant boundary, the workspace, the id, the key and the schema are immutable across
        // a rename (threat T5): renaming never moves the type or changes its natural key.
        Assert.Equal(_organizationId, entityType.OrganizationId);
        Assert.Equal(_workspaceId, entityType.WorkspaceId);
        Assert.Equal("type-alpha", entityType.TypeKey);
        Assert.Equal(_validSchema, entityType.AttributeSchema);
    }

    [Fact]
    public void Rename_trims_the_display_name()
    {
        var entityType = CreateEntityType();

        entityType.Rename("  Spaced  ", _updatedAt);

        Assert.Equal("Spaced", entityType.DisplayName);
    }

    [Fact]
    public void Rename_rejects_an_invalid_name_and_leaves_the_type_untouched()
    {
        var entityType = CreateEntityType();

        Assert.Throws<ArgumentException>(() => entityType.Rename("   ", _updatedAt));

        // The rejected rename did not change the display name or re-stamp the update timestamp.
        Assert.Equal("Type Alpha", entityType.DisplayName);
        Assert.Equal(_createdAt, entityType.UpdatedAt);
    }

    // --- RedefineAttributeSchema (reject-with-no-mutation) ----------------------

    [Fact]
    public void RedefineAttributeSchema_replaces_the_schema_and_timestamp_only()
    {
        var entityType = CreateEntityType();
        const string newSchema = """{"fields":[{"name":"count","kind":"number"}]}""";

        entityType.RedefineAttributeSchema(newSchema, _updatedAt);

        Assert.Equal(newSchema, entityType.AttributeSchema);
        Assert.Equal(_updatedAt, entityType.UpdatedAt);
        // The identity, tenant, workspace, key and display name are immutable here (threat T5).
        Assert.Equal(_organizationId, entityType.OrganizationId);
        Assert.Equal(_workspaceId, entityType.WorkspaceId);
        Assert.Equal("type-alpha", entityType.TypeKey);
        Assert.Equal("Type Alpha", entityType.DisplayName);
    }

    [Fact]
    public void RedefineAttributeSchema_trims_the_schema()
    {
        var entityType = CreateEntityType();
        const string newSchema = """{"x":1}""";

        entityType.RedefineAttributeSchema("  " + newSchema + "  ", _updatedAt);

        Assert.Equal(newSchema, entityType.AttributeSchema);
    }

    [Fact]
    public void RedefineAttributeSchema_rejects_a_malformed_schema_and_leaves_the_type_untouched()
    {
        var entityType = CreateEntityType();

        Assert.Throws<ArgumentException>(() => entityType.RedefineAttributeSchema("{ not json", _updatedAt));

        // The rejected redefine left the prior schema and update timestamp intact.
        Assert.Equal(_validSchema, entityType.AttributeSchema);
        Assert.Equal(_createdAt, entityType.UpdatedAt);
    }

    // --- Log safety ------------------------------------------------------------

    [Fact]
    public void ToString_exposes_identifiers_and_key_but_no_name_or_schema()
    {
        // Log-safety (threat T7): structured logs carry identifiers and the canonical key,
        // never the display name or the attribute-schema content (template-/host-supplied data).
        var entityType = EntityType.Create(
            _organizationId,
            _workspaceId,
            "type-alpha",
            "Secret Display Name",
            """{"secret":"do-not-log-this"}""",
            _createdAt);

        var text = entityType.ToString();

        Assert.Contains(entityType.Id.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_organizationId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains(_workspaceId.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("key=type-alpha", text, StringComparison.Ordinal);
        // Neither the display name nor the schema content is written to logs.
        Assert.DoesNotContain("Secret Display Name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this", text, StringComparison.Ordinal);
    }
}
