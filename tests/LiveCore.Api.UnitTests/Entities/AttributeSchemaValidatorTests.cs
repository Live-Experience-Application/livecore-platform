using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for <see cref="AttributeSchemaValidator"/> (CORE-ENT-001) — the entity type's
/// attribute-schema TEMPLATE VALIDATION in isolation (the story's "Template validation tests").
///
/// The validator decides ONLY whether a template-defined attribute schema is WELL-FORMED JSON
/// within a size bound; it never inspects the schema's vocabulary and never validates it against a
/// template's expected structure (that is CORE-ENT-004). THE TEMPLATE BOUNDARY (docs/04): the
/// schema content is DATA, so the validator must stay generic and free of type-specific logic.
/// </summary>
public class AttributeSchemaValidatorTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"fields":[{"name":"label","kind":"text"},{"name":"count","kind":"number"}]}""")]
    [InlineData("[1,2,3]")]
    [InlineData("\"scalar\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public void IsValidSchema_accepts_well_formed_json(string schema)
        => Assert.True(AttributeSchemaValidator.IsValidSchema(schema));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{\"a\":}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,2,")]
    [InlineData("not json at all")]
    public void IsValidSchema_rejects_blank_or_malformed_json(string? schema)
        => Assert.False(AttributeSchemaValidator.IsValidSchema(schema));

    [Fact]
    public void IsValidSchema_accepts_a_document_at_the_size_bound()
    {
        // A JSON string whose total length is exactly the bound is accepted (inclusive bound).
        var innerLength = AttributeSchemaValidator.MaxSchemaLength - 2; // two quote characters
        var atBound = "\"" + new string('a', innerLength) + "\"";

        Assert.Equal(AttributeSchemaValidator.MaxSchemaLength, atBound.Length);
        Assert.True(AttributeSchemaValidator.IsValidSchema(atBound));
    }

    [Fact]
    public void IsValidSchema_rejects_a_document_over_the_size_bound()
    {
        // Well-formed JSON one character over the bound is rejected; the length is checked before
        // parsing, so an oversized body never reaches the parser.
        var atBound = "\"" + new string('a', AttributeSchemaValidator.MaxSchemaLength - 1) + "\"";

        Assert.Equal(AttributeSchemaValidator.MaxSchemaLength + 1, atBound.Length);
        Assert.False(AttributeSchemaValidator.IsValidSchema(atBound));
    }
}
