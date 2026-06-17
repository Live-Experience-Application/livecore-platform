// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Organizations;

namespace LiveCore.Api.UnitTests.Organizations;

/// <summary>
/// Unit tests for the <see cref="Organization"/> invariants (CORE-ID-003):
/// the organization is the tenant root addressed by an immutable surrogate id
/// and an immutable, canonical, globally unique slug; the display name is
/// metadata only and never a tenant boundary (threats T5/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). ToString stays log-safe (threat T7).
/// </summary>
public class OrganizationTests
{
    private const string _slug = "northwind-labs";
    private const string _name = "Northwind Labs";

    private static readonly DateTimeOffset _createdAt = new(2026, 6, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _updatedAt = new(2026, 6, 11, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_copies_slug_and_name()
    {
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.NotEqual(Guid.Empty, organization.Id);
        Assert.Equal(_slug, organization.Slug);
        Assert.Equal(_name, organization.Name);
        Assert.Equal(_createdAt, organization.CreatedAt);
        Assert.Equal(_createdAt, organization.UpdatedAt);
    }

    [Fact]
    public void Create_generates_time_ordered_unique_ids()
    {
        var first = Organization.Create(_slug, _name, _createdAt);
        var second = Organization.Create("acme-co", _name, _createdAt);

        // UUID version 7 per docs/10_DATABASE_SCHEMA.md (time-ordered ids).
        Assert.Equal(7, first.Id.Version);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_canonicalizes_slug_to_lower_case_and_trims()
    {
        var organization = Organization.Create("  NorthWind-Labs  ", _name, _createdAt);

        Assert.Equal("northwind-labs", organization.Slug);
    }

    [Fact]
    public void Create_trims_name()
    {
        var organization = Organization.Create(_slug, "  Northwind Labs  ", _createdAt);

        Assert.Equal("Northwind Labs", organization.Name);
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc()
    {
        var localCreatedAt = new DateTimeOffset(2026, 6, 11, 10, 0, 0, TimeSpan.FromHours(2));

        var organization = Organization.Create(_slug, _name, localCreatedAt);

        Assert.Equal(TimeSpan.Zero, organization.CreatedAt.Offset);
        Assert.Equal(localCreatedAt.ToUniversalTime(), organization.CreatedAt);
    }

    [Theory]
    [InlineData("a")] // shorter than the minimum length
    [InlineData("-leading")] // leading dash
    [InlineData("trailing-")] // trailing dash
    [InlineData("double--dash")] // doubled dash
    [InlineData("under_score")] // disallowed character
    [InlineData("white space")] // whitespace inside
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_invalid_slugs(string invalidSlug)
    {
        // Values here are invalid even after lower-casing/trimming. Casing-only
        // differences (for example "Mixed-Case") are canonicalized to a valid
        // slug instead and are covered by a separate test.
        Assert.Throws<ArgumentException>(() => Organization.Create(invalidSlug, _name, _createdAt));
    }

    [Fact]
    public void Create_accepts_a_mixed_case_slug_by_canonicalizing_it()
    {
        var organization = Organization.Create("Mixed-Case", _name, _createdAt);

        Assert.Equal("mixed-case", organization.Slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_names(string? blankName)
    {
        Assert.Throws<ArgumentException>(() => Organization.Create(_slug, blankName!, _createdAt));
    }

    [Fact]
    public void Create_rejects_name_with_control_characters()
    {
        Assert.Throws<ArgumentException>(() => Organization.Create(_slug, "Bad\tName", _createdAt));
    }

    [Fact]
    public void Create_rejects_name_over_the_length_limit()
    {
        var tooLong = new string('x', Organization.MaxNameLength + 1);

        Assert.Throws<ArgumentException>(() => Organization.Create(_slug, tooLong, _createdAt));
    }

    [Fact]
    public void Create_rejects_slug_over_the_length_limit()
    {
        var tooLong = new string('a', Organization.MaxSlugLength + 1);

        Assert.Throws<ArgumentException>(() => Organization.Create(tooLong, _name, _createdAt));
    }

    [Fact]
    public void Organization_is_addressed_only_by_its_exact_slug()
    {
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.True(organization.HasSlug(_slug));
        Assert.False(organization.HasSlug("acme-co"));
    }

    [Fact]
    public void Slug_matching_is_case_sensitive_against_the_stored_canonical_value()
    {
        // The stored slug is canonical (lower-case); an upper-cased argument
        // is a different string and must never address the tenant (threat T5).
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.False(organization.HasSlug(_slug.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasSlug_returns_false_for_blank_values(string? blankSlug)
    {
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.False(organization.HasSlug(blankSlug));
    }

    [Fact]
    public void Organization_is_addressed_only_by_its_exact_id()
    {
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.True(organization.HasId(organization.Id));
        Assert.False(organization.HasId(Guid.CreateVersion7()));
        Assert.False(organization.HasId(Guid.Empty));
    }

    [Fact]
    public void Rename_changes_the_name_and_updated_at_but_not_the_slug_or_id()
    {
        var organization = Organization.Create(_slug, _name, _createdAt);
        var originalId = organization.Id;

        organization.Rename("Northwind Research", _updatedAt);

        Assert.Equal("Northwind Research", organization.Name);
        Assert.Equal(_updatedAt, organization.UpdatedAt);
        Assert.Equal(_createdAt, organization.CreatedAt);
        Assert.Equal(_slug, organization.Slug);
        Assert.Equal(originalId, organization.Id);
    }

    [Fact]
    public void Rename_rejects_an_invalid_name_and_leaves_the_organization_untouched()
    {
        var organization = Organization.Create(_slug, _name, _createdAt);

        Assert.Throws<ArgumentException>(() => organization.Rename("   ", _updatedAt));

        Assert.Equal(_name, organization.Name);
        Assert.Equal(_createdAt, organization.UpdatedAt);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("northwind-labs")]
    [InlineData("a1-b2-c3")]
    [InlineData("0rg")]
    public void IsValidSlug_accepts_canonical_slugs(string slug)
        => Assert.True(Organization.IsValidSlug(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("UPPER")]
    [InlineData("-x")]
    [InlineData("x-")]
    [InlineData("a--b")]
    [InlineData("a b")]
    [InlineData("a_b")]
    public void IsValidSlug_rejects_non_canonical_slugs(string? slug)
        => Assert.False(Organization.IsValidSlug(slug));

    [Fact]
    public void CanonicalizeSlug_lower_cases_and_trims_a_valid_value()
        => Assert.Equal("northwind-labs", Organization.CanonicalizeSlug("  NorthWind-Labs "));

    [Fact]
    public void CanonicalizeSlug_rejects_a_value_that_is_not_a_slug_even_after_casing()
        => Assert.Throws<ArgumentException>(() => Organization.CanonicalizeSlug("not a slug"));

    [Fact]
    public void ToString_exposes_identifiers_but_not_the_display_name()
    {
        // Log-safety (threat T7): structured logs may carry identifiers, but
        // never the free-form display name.
        var organization = Organization.Create(_slug, "Secret Tenant Name", _createdAt);

        var text = organization.ToString();

        Assert.Contains(_slug, text, StringComparison.Ordinal);
        Assert.Contains(organization.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret Tenant Name", text, StringComparison.OrdinalIgnoreCase);
    }
}
