// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Unit tests for <see cref="AttributeValuesValidator"/> (CORE-ENT-002) — the entity instance's
/// attribute-values TEMPLATE VALIDATION in isolation (the story's "Template validation tests").
///
/// The validator decides ONLY whether an instance's attribute values are WELL-FORMED JSON within a
/// size bound; it never inspects the values' vocabulary and never validates them against the entity
/// type's declared attribute schema (schema-conformance is CORE-ENT-004). THE TEMPLATE BOUNDARY
/// (docs/04): the values are DATA, so the validator must stay generic and free of type-specific
/// logic.
/// </summary>
public class AttributeValuesValidatorTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"label":"value","count":3}""")]
    [InlineData("[1,2,3]")]
    [InlineData("\"scalar\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public void IsValidValues_accepts_well_formed_json(string values)
        => Assert.True(AttributeValuesValidator.IsValidValues(values));

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
    public void IsValidValues_rejects_blank_or_malformed_json(string? values)
        => Assert.False(AttributeValuesValidator.IsValidValues(values));

    [Fact]
    public void IsValidValues_accepts_a_document_at_the_size_bound()
    {
        // A JSON string whose total length is exactly the bound is accepted (inclusive bound).
        var innerLength = AttributeValuesValidator.MaxValuesLength - 2; // two quote characters
        var atBound = "\"" + new string('a', innerLength) + "\"";

        Assert.Equal(AttributeValuesValidator.MaxValuesLength, atBound.Length);
        Assert.True(AttributeValuesValidator.IsValidValues(atBound));
    }

    [Fact]
    public void IsValidValues_rejects_a_document_over_the_size_bound()
    {
        // Well-formed JSON one character over the bound is rejected; the length is checked before
        // parsing, so an oversized body never reaches the parser.
        var overBound = "\"" + new string('a', AttributeValuesValidator.MaxValuesLength - 1) + "\"";

        Assert.Equal(AttributeValuesValidator.MaxValuesLength + 1, overBound.Length);
        Assert.False(AttributeValuesValidator.IsValidValues(overBound));
    }
}
