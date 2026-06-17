// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entities;

namespace LiveCore.Api.UnitTests.Entities;

/// <summary>
/// Tests for <see cref="EntitySearchCriteria"/> (CORE-ENT-005) — the generic, product-neutral search
/// criteria. They cover normalization (trimming, blank-to-null name), validation (over-long term and
/// empty type id rejected), and the deterministic ORDINAL case-insensitive name match. All fixtures
/// are generic ("alpha"/"beta"); no vertical vocabulary appears (the template boundary, docs/04).
/// </summary>
public sealed class EntitySearchCriteriaTests
{
    [Fact]
    public void MatchAll_has_no_filters()
    {
        var criteria = EntitySearchCriteria.MatchAll;

        Assert.Null(criteria.NameContains);
        Assert.Null(criteria.EntityTypeId);
        Assert.False(criteria.HasNameFilter);
    }

    [Fact]
    public void Create_with_no_arguments_matches_all()
    {
        var criteria = EntitySearchCriteria.Create();

        Assert.Null(criteria.NameContains);
        Assert.Null(criteria.EntityTypeId);
        Assert.False(criteria.HasNameFilter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_name_term_normalizes_to_no_name_filter(string? term)
    {
        var criteria = EntitySearchCriteria.Create(term);

        Assert.Null(criteria.NameContains);
        Assert.False(criteria.HasNameFilter);
    }

    [Fact]
    public void Name_term_is_trimmed()
    {
        var criteria = EntitySearchCriteria.Create("  alpha  ");

        Assert.Equal("alpha", criteria.NameContains);
        Assert.True(criteria.HasNameFilter);
    }

    [Fact]
    public void Over_long_name_term_is_rejected()
    {
        var term = new string('a', EntitySearchCriteria.MaxNameContainsLength + 1);

        var exception = Assert.Throws<ArgumentException>(() => EntitySearchCriteria.Create(term));
        Assert.Equal("nameContains", exception.ParamName);
    }

    [Fact]
    public void Name_term_at_the_maximum_length_is_accepted()
    {
        var term = new string('a', EntitySearchCriteria.MaxNameContainsLength);

        var criteria = EntitySearchCriteria.Create(term);

        Assert.Equal(term, criteria.NameContains);
    }

    [Fact]
    public void Empty_type_filter_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => EntitySearchCriteria.Create(entityTypeId: Guid.Empty));
        Assert.Equal("entityTypeId", exception.ParamName);
    }

    [Fact]
    public void Type_filter_is_carried_through()
    {
        var typeId = Guid.NewGuid();

        var criteria = EntitySearchCriteria.Create(entityTypeId: typeId);

        Assert.Equal(typeId, criteria.EntityTypeId);
    }

    [Fact]
    public void MatchesName_with_no_filter_matches_every_name()
    {
        var criteria = EntitySearchCriteria.MatchAll;

        Assert.True(criteria.MatchesName("alpha"));
        Assert.True(criteria.MatchesName("anything at all"));
    }

    [Theory]
    [InlineData("alpha", "alpha", true)]
    [InlineData("alp", "alpha", true)]
    [InlineData("PHA", "alpha", true)] // case-insensitive
    [InlineData("ALPHA", "alpha", true)] // case-insensitive
    [InlineData("beta", "alpha", false)]
    [InlineData("alphabet", "alpha", false)] // term longer than the name does not match
    public void MatchesName_is_ordinal_case_insensitive_substring(string term, string name, bool expected)
    {
        var criteria = EntitySearchCriteria.Create(term);

        Assert.Equal(expected, criteria.MatchesName(name));
    }
}
