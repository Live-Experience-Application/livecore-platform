// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Templates;

namespace LiveCore.Api.UnitTests.Templates;

/// <summary>
/// The story's "Template validation tests": unit tests for
/// <see cref="TemplateDefinitionValidator"/> (CORE-ENT-004). They prove the GENERIC structural
/// validation of a template definition: a well-formed, correctly-structured definition is accepted
/// and parses to the loader's <see cref="TemplateDefinition"/> model; malformed JSON is rejected; a
/// missing/empty/non-array <c>entityTypes</c> is rejected; and an entity-type entry with an invalid
/// key, display name or attribute schema is rejected.
///
/// Scope boundary (THE TEMPLATE BOUNDARY, docs/04_PRODUCT_BOUNDARIES.md): validation is SHAPE only —
/// the validator never inspects the vocabulary of any key/name and never validates an entity
/// INSTANCE against a schema. Every fixture here is generic and neutral ("sample.template",
/// "type-alpha", "profile", {"fields":[]}); no vertical vocabulary appears (AGENTS.md,
/// csv/forbidden_core_terms.csv).
/// </summary>
public class TemplateDefinitionValidatorTests
{
    // --- Accepted -------------------------------------------------------------

    [Fact]
    public void A_well_formed_correctly_structured_definition_is_accepted_and_parses()
    {
        const string definition = """
            {
              "templateKey": "sample.template",
              "entityTypes": [
                { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": { "fields": [] } },
                { "key": "profile", "displayName": "Profile", "attributeSchema": { "fields": [ { "name": "label" } ] } }
              ]
            }
            """;

        Assert.True(TemplateDefinitionValidator.IsValidDefinition(definition));
        Assert.True(TemplateDefinitionValidator.TryParse(definition, out var parsed));
        Assert.NotNull(parsed);

        Assert.Equal("sample.template", parsed.TemplateKey);
        Assert.Equal(2, parsed.EntityTypeDefinitions.Count);

        var first = parsed.EntityTypeDefinitions[0];
        Assert.Equal("type-alpha", first.Key);
        Assert.Equal("Type Alpha", first.DisplayName);
        // The attribute schema is captured as raw JSON text, ready for EntityType.Create.
        Assert.Contains("\"fields\"", first.AttributeSchema, StringComparison.Ordinal);

        var second = parsed.EntityTypeDefinitions[1];
        Assert.Equal("profile", second.Key);
        Assert.Equal("Profile", second.DisplayName);
    }

    [Fact]
    public void A_definition_with_a_re_cased_entity_type_key_is_accepted()
    {
        // The validator accepts a key whose canonical (lower/trimmed) form is a valid type-key
        // shape; the loader canonicalizes via EntityType.Create. A mixed-case key is therefore
        // valid here.
        const string definition = """
            {
              "templateKey": "sample.template",
              "entityTypes": [
                { "key": "Type-Alpha", "displayName": "Type Alpha", "attributeSchema": {} }
              ]
            }
            """;

        Assert.True(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    // --- Malformed JSON -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3")]
    public void Malformed_or_blank_json_is_rejected(string definition)
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
        Assert.False(TemplateDefinitionValidator.TryParse(definition, out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("\"a string\"")] // scalar root
    [InlineData("42")] // number root
    [InlineData("[]")] // array root
    public void A_non_object_root_is_rejected(string definition)
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    // --- templateKey structure ------------------------------------------------

    [Fact]
    public void A_missing_top_level_template_key_is_rejected()
    {
        const string definition = """
            { "entityTypes": [ { "key": "type-alpha", "displayName": "A", "attributeSchema": {} } ] }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    [Fact]
    public void A_top_level_template_key_with_a_bad_shape_is_rejected()
    {
        const string definition = """
            {
              "templateKey": "Not A Valid Key",
              "entityTypes": [ { "key": "type-alpha", "displayName": "A", "attributeSchema": {} } ]
            }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    [Fact]
    public void A_non_string_template_key_is_rejected()
    {
        const string definition = """
            {
              "templateKey": 123,
              "entityTypes": [ { "key": "type-alpha", "displayName": "A", "attributeSchema": {} } ]
            }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    // --- entityTypes structure ------------------------------------------------

    [Fact]
    public void A_missing_entity_types_array_is_rejected()
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition("""{"templateKey":"sample.template"}"""));
    }

    [Fact]
    public void An_empty_entity_types_array_is_rejected()
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(
            """{"templateKey":"sample.template","entityTypes":[]}"""));
    }

    [Fact]
    public void A_non_array_entity_types_is_rejected()
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(
            """{"templateKey":"sample.template","entityTypes":{"key":"type-alpha"}}"""));
    }

    [Fact]
    public void A_non_object_entity_type_entry_is_rejected()
    {
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(
            """{"templateKey":"sample.template","entityTypes":["type-alpha"]}"""));
    }

    // --- Per-entry field validation -------------------------------------------

    [Fact]
    public void An_entry_with_an_invalid_key_is_rejected()
    {
        const string definition = """
            {
              "templateKey": "sample.template",
              "entityTypes": [ { "key": "bad key!", "displayName": "A", "attributeSchema": {} } ]
            }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    [Fact]
    public void An_entry_with_a_missing_or_blank_display_name_is_rejected()
    {
        const string missing = """
            { "templateKey": "sample.template", "entityTypes": [ { "key": "type-alpha", "attributeSchema": {} } ] }
            """;
        const string blank = """
            {
              "templateKey": "sample.template",
              "entityTypes": [ { "key": "type-alpha", "displayName": "   ", "attributeSchema": {} } ]
            }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(missing));
        Assert.False(TemplateDefinitionValidator.IsValidDefinition(blank));
    }

    [Fact]
    public void An_entry_with_a_missing_attribute_schema_is_rejected()
    {
        const string definition = """
            { "templateKey": "sample.template", "entityTypes": [ { "key": "type-alpha", "displayName": "A" } ] }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }

    [Fact]
    public void An_entry_whose_first_is_valid_but_second_is_invalid_rejects_the_whole_definition()
    {
        // One bad entry fails the whole definition (the loader requires every entry to be valid).
        const string definition = """
            {
              "templateKey": "sample.template",
              "entityTypes": [
                { "key": "type-alpha", "displayName": "Type Alpha", "attributeSchema": {} },
                { "key": "bad key!", "displayName": "Bad", "attributeSchema": {} }
              ]
            }
            """;

        Assert.False(TemplateDefinitionValidator.IsValidDefinition(definition));
    }
}
