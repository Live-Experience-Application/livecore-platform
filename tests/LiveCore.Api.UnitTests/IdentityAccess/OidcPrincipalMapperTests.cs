using System.Security.Claims;
using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for the OIDC claims normalization of the IdentityAccess module
/// (CORE-ID-001): valid claim sets, missing/conflicting/malformed claims and
/// negative cases for foreign-organization claims at the principal level
/// (threat T5 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public class OidcPrincipalMapperTests
{
    private const string _issuer = "https://id.example.test/realms/livecore";
    private const string _subject = "9f8d2c1e-0b4a-4f6d-9a3c-d1e2f3a4b5c6";

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Bearer"));

    private static ClaimsPrincipal AuthenticatedPrincipalWithRequiredClaims(params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new(OidcClaimTypes.Issuer, _issuer),
            new(OidcClaimTypes.Subject, _subject),
        };
        claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public void Map_builds_user_principal_from_valid_oidc_claims()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Name, "Casey Example"),
            new Claim(OidcClaimTypes.Email, "casey@example.test"),
            new Claim(OidcClaimTypes.Organization, "org-a"),
            new Claim(OidcClaimTypes.Organization, "org-b")));

        Assert.True(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.None, result.Error);
        var principal = result.Principal!;
        Assert.Equal(PrincipalType.User, principal.Type);
        Assert.Equal(_issuer, principal.Issuer);
        Assert.Equal(_subject, principal.SubjectId);
        Assert.Equal("Casey Example", principal.DisplayName);
        Assert.Equal("casey@example.test", principal.Email);
        Assert.Equal(2, principal.OrganizationClaims.Count);
        Assert.True(principal.HasOrganizationClaim("org-a"));
        Assert.True(principal.HasOrganizationClaim("org-b"));
    }

    [Fact]
    public void Map_succeeds_with_only_issuer_and_subject()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims());

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(PrincipalType.User, principal.Type);
        Assert.Null(principal.DisplayName);
        Assert.Null(principal.Email);
        Assert.Empty(principal.OrganizationClaims);
    }

    [Fact]
    public void Map_builds_service_account_principal_when_client_id_claim_is_present()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.ClientId, "automation-client")));

        Assert.True(result.Succeeded);
        Assert.Equal(PrincipalType.ServiceAccount, result.Principal!.Type);
    }

    [Fact]
    public void Map_treats_blank_client_id_claim_as_user_principal()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.ClientId, "   ")));

        Assert.True(result.Succeeded);
        Assert.Equal(PrincipalType.User, result.Principal!.Type);
    }

    [Fact]
    public void Map_prefers_name_claim_over_preferred_username_for_display_name()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Name, "Casey Example"),
            new Claim(OidcClaimTypes.PreferredUsername, "casey.example")));

        Assert.True(result.Succeeded);
        Assert.Equal("Casey Example", result.Principal!.DisplayName);
    }

    [Fact]
    public void Map_falls_back_to_preferred_username_when_name_claim_is_absent()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.PreferredUsername, "casey.example")));

        Assert.True(result.Succeeded);
        Assert.Equal("casey.example", result.Principal!.DisplayName);
    }

    [Fact]
    public void Map_falls_back_to_preferred_username_when_name_claims_conflict()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Name, "Casey Example"),
            new Claim(OidcClaimTypes.Name, "Robin Example"),
            new Claim(OidcClaimTypes.PreferredUsername, "casey.example")));

        Assert.True(result.Succeeded);
        Assert.Equal("casey.example", result.Principal!.DisplayName);
    }

    [Fact]
    public void Map_trims_optional_claim_values()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Name, "  Casey Example  ")));

        Assert.True(result.Succeeded);
        Assert.Equal("Casey Example", result.Principal!.DisplayName);
    }

    [Fact]
    public void Map_treats_conflicting_email_claims_as_absent()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Email, "casey@example.test"),
            new Claim(OidcClaimTypes.Email, "other@example.test")));

        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.Email);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.test")]
    [InlineData("casey@")]
    [InlineData("   ")]
    public void Map_treats_malformed_email_claim_as_absent(string emailClaimValue)
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Email, emailClaimValue)));

        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.Email);
    }

    [Fact]
    public void Map_fails_for_unauthenticated_principal()
    {
        // Same claims as a valid token, but the identity was never
        // authenticated: no principal may be derived from it.
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject),
        });

        var result = OidcPrincipalMapper.Map(new ClaimsPrincipal(identity));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.NotAuthenticated, result.Error);
        Assert.Null(result.Principal);
    }

    [Fact]
    public void Map_fails_when_issuer_claim_is_missing()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Subject, _subject)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.MissingIssuer, result.Error);
    }

    [Fact]
    public void Map_fails_when_subject_claim_is_missing()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.MissingSubject, result.Error);
    }

    [Fact]
    public void Map_fails_when_conflicting_issuer_claims_are_present()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Issuer, "https://other.example.test/realms/livecore"),
            new Claim(OidcClaimTypes.Subject, _subject)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.AmbiguousIssuer, result.Error);
    }

    [Fact]
    public void Map_fails_when_conflicting_subject_claims_are_present()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject),
            new Claim(OidcClaimTypes.Subject, "another-subject")));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.AmbiguousSubject, result.Error);
    }

    [Fact]
    public void Map_accepts_repeated_identical_subject_claims()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject),
            new Claim(OidcClaimTypes.Subject, _subject)));

        Assert.True(result.Succeeded);
        Assert.Equal(_subject, result.Principal!.SubjectId);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("not-an-absolute-uri")]
    [InlineData("/relative/path")]
    [InlineData("ftp://id.example.test")]
    [InlineData("https://id.example.test/realms/livecore?probe=1")]
    [InlineData("https://id.example.test/realms/livecore#fragment")]
    public void Map_fails_when_issuer_is_not_a_clean_absolute_http_uri(string issuerClaimValue)
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, issuerClaimValue),
            new Claim(OidcClaimTypes.Subject, _subject)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.InvalidIssuer, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sub\u0007ject")]
    public void Map_fails_when_subject_is_blank_or_contains_control_characters(string subjectClaimValue)
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, subjectClaimValue)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.InvalidSubject, result.Error);
    }

    [Fact]
    public void Map_fails_when_subject_exceeds_the_maximum_length()
    {
        var overlongSubject = new string('s', OidcPrincipal.MaxSubjectIdLength + 1);

        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipal(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, overlongSubject)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.InvalidSubject, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("org\u0007a")]
    public void Map_fails_when_organization_claim_value_is_blank_or_contains_control_characters(
        string organizationClaimValue)
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Organization, organizationClaimValue)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.InvalidOrganizationClaim, result.Error);
        Assert.Null(result.Principal);
    }

    [Fact]
    public void Map_fails_when_organization_claim_value_exceeds_the_maximum_length()
    {
        var overlongValue = new string('o', OidcPrincipal.MaxOrganizationClaimLength + 1);

        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Organization, overlongValue)));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.InvalidOrganizationClaim, result.Error);
    }

    [Fact]
    public void Map_deduplicates_repeated_organization_claims()
    {
        var result = OidcPrincipalMapper.Map(AuthenticatedPrincipalWithRequiredClaims(
            new Claim(OidcClaimTypes.Organization, "org-a"),
            new Claim(OidcClaimTypes.Organization, "org-a")));

        Assert.True(result.Succeeded);
        Assert.Single(result.Principal!.OrganizationClaims);
        Assert.True(result.Principal.HasOrganizationClaim("org-a"));
    }

    [Fact]
    public void Map_ignores_claims_from_unauthenticated_identities()
    {
        // Negative foreign-tenant case at the principal level (threat T5):
        // an unauthenticated identity attached to the same ClaimsPrincipal
        // must not smuggle a different _subject or a foreign organization
        // claim into the principal.
        var authenticated = new ClaimsIdentity(
            new[]
            {
                new Claim(OidcClaimTypes.Issuer, _issuer),
                new Claim(OidcClaimTypes.Subject, _subject),
                new Claim(OidcClaimTypes.Organization, "org-a"),
            },
            "Bearer");
        var unauthenticated = new ClaimsIdentity(new[]
        {
            new Claim(OidcClaimTypes.Subject, "smuggled-subject"),
            new Claim(OidcClaimTypes.Organization, "org-foreign"),
        });

        var result = OidcPrincipalMapper.Map(new ClaimsPrincipal(new[] { authenticated, unauthenticated }));

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal(_subject, principal.SubjectId);
        Assert.Single(principal.OrganizationClaims);
        Assert.True(principal.HasOrganizationClaim("org-a"));
        Assert.False(principal.HasOrganizationClaim("org-foreign"));
    }

    [Fact]
    public void Map_rejects_multiple_authenticated_identities()
    {
        // Negative foreign-tenant case at the principal level (threat T5):
        // a second authenticated identity without its own iss/sub claims
        // must not have its organization claims attributed to the first
        // identity's (iss, sub). Mapping fails instead of merging.
        var first = new ClaimsIdentity(
            new[]
            {
                new Claim(OidcClaimTypes.Issuer, _issuer),
                new Claim(OidcClaimTypes.Subject, _subject),
                new Claim(OidcClaimTypes.Organization, "org-a"),
            },
            "Bearer");
        var second = new ClaimsIdentity(
            new[] { new Claim(OidcClaimTypes.Organization, "org-foreign") },
            "SecondScheme");

        var result = OidcPrincipalMapper.Map(new ClaimsPrincipal(new[] { first, second }));

        Assert.False(result.Succeeded);
        Assert.Equal(OidcPrincipalMappingError.AmbiguousIdentity, result.Error);
        Assert.Null(result.Principal);
    }

    [Fact]
    public void Map_throws_for_null_claims_principal()
    {
        Assert.Throws<ArgumentNullException>(() => OidcPrincipalMapper.Map(null!));
    }
}
