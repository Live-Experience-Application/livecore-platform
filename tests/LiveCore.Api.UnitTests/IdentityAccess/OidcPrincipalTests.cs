// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for the <see cref="OidcPrincipal"/> invariants (CORE-ID-001),
/// including the negative foreign-organization checks at the principal level
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md) and the log-safety rule
/// for personal metadata (threat T7).
/// </summary>
public class OidcPrincipalTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private static readonly string _controlCharacter = ((char)7).ToString();

    private static OidcPrincipal CreatePrincipal(params string[] organizationClaims)
        => new(PrincipalType.User, _issuer, _subject, organizationClaims: organizationClaims);

    [Fact]
    public void Principal_does_not_have_foreign_organization_claim()
    {
        var principal = CreatePrincipal("org-a");

        Assert.True(principal.HasOrganizationClaim("org-a"));
        Assert.False(principal.HasOrganizationClaim("org-b"));
    }

    [Fact]
    public void Principal_without_organization_claims_has_no_organization_claim()
    {
        var principal = CreatePrincipal();

        Assert.Empty(principal.OrganizationClaims);
        Assert.False(principal.HasOrganizationClaim("org-a"));
    }

    [Fact]
    public void Organization_claim_matching_is_case_sensitive()
    {
        // Case variance must never cross the tenant boundary: an asserted
        // "org-a" claim grants nothing for "ORG-A".
        var principal = CreatePrincipal("org-a");

        Assert.False(principal.HasOrganizationClaim("ORG-A"));
        Assert.False(principal.HasOrganizationClaim("Org-A"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasOrganizationClaim_returns_false_for_blank_values(string? organizationClaimValue)
    {
        var principal = CreatePrincipal("org-a");

        Assert.False(principal.HasOrganizationClaim(organizationClaimValue));
    }

    [Fact]
    public void Organization_claims_cannot_be_changed_after_construction()
    {
        var source = new List<string> { "org-a" };
        var principal = new OidcPrincipal(PrincipalType.User, _issuer, _subject, organizationClaims: source);

        source.Add("org-b");

        Assert.Single(principal.OrganizationClaims);
        Assert.False(principal.HasOrganizationClaim("org-b"));
    }

    [Fact]
    public void Organization_claims_cannot_be_mutated_through_casting()
    {
        var principal = CreatePrincipal("org-a");

        // The exposed set must not be castable back to a mutable
        // implementation; otherwise a caller could widen the tenant
        // boundary after construction (threat T5).
        Assert.IsNotType<HashSet<string>>(principal.OrganizationClaims);
        var asCollection = Assert.IsAssignableFrom<ICollection<string>>(principal.OrganizationClaims);
        Assert.True(asCollection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asCollection.Add("org-foreign"));
        Assert.False(principal.HasOrganizationClaim("org-foreign"));
    }

    [Fact]
    public void Constructor_deduplicates_organization_claims()
    {
        var principal = CreatePrincipal("org-a", "org-a");

        Assert.Single(principal.OrganizationClaims);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-absolute-uri")]
    [InlineData("ftp://id.example.test")]
    [InlineData("https://id.example.test/realms/livecore?probe=1")]
    [InlineData("https://id.example.test/realms/livecore#fragment")]
    public void Constructor_rejects_invalid_issuer(string? invalidIssuer)
    {
        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, invalidIssuer!, _subject));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_blank_subject(string? invalidSubject)
    {
        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, _issuer, invalidSubject!));
    }

    [Fact]
    public void Constructor_rejects_subject_with_control_characters()
    {
        var subjectWithControlCharacter = "sub" + _controlCharacter + "ject";

        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, _issuer, subjectWithControlCharacter));
    }

    [Fact]
    public void Constructor_rejects_overlong_subject()
    {
        var overlongSubject = new string('s', OidcPrincipal.MaxSubjectIdLength + 1);

        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, _issuer, overlongSubject));
    }

    [Fact]
    public void Constructor_rejects_invalid_organization_claim_values()
    {
        var valueWithControlCharacter = "org" + _controlCharacter + "a";

        Assert.Throws<ArgumentException>(() => CreatePrincipal("   "));
        Assert.Throws<ArgumentException>(() => CreatePrincipal(valueWithControlCharacter));
        Assert.Throws<ArgumentException>(() => CreatePrincipal((string)null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Constructor_rejects_undefined_principal_type(int undefinedTypeValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OidcPrincipal((PrincipalType)undefinedTypeValue, _issuer, _subject));
    }

    [Fact]
    public void Constructor_rejects_blank_display_name()
    {
        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, _issuer, _subject, displayName: "   "));
    }

    [Fact]
    public void Constructor_rejects_malformed_email()
    {
        Assert.Throws<ArgumentException>(
            () => new OidcPrincipal(PrincipalType.User, _issuer, _subject, email: "not-an-email"));
    }

    [Fact]
    public void ToString_exposes_identifiers_but_not_personal_metadata()
    {
        // Log-safety (threat T7): structured logs may carry identifiers, but
        // never display names or email addresses.
        var principal = new OidcPrincipal(
            PrincipalType.User,
            _issuer,
            _subject,
            displayName: "Casey Example",
            email: "casey@example.test",
            organizationClaims: new[] { "org-a" });

        var text = principal.ToString();

        Assert.Contains(_subject, text, StringComparison.Ordinal);
        Assert.Contains(_issuer, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Casey Example", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("casey@example.test", text, StringComparison.OrdinalIgnoreCase);
    }
}
