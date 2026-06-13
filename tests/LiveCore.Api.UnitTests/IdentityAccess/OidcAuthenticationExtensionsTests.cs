using LiveCore.Api.IdentityAccess;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="OidcAuthenticationExtensions.IsMissingProductionAudience"/> (CORE-OPS-004) —
/// the pure decision behind the startup guard that makes the OIDC Audience effectively mandatory in a
/// Production environment. A configured Authority accepts tokens, but audience validation is enabled only
/// when an Audience is configured, so a blank Audience silently disables audience scoping (any token the
/// issuer signs is accepted). These pin the matrix: in Production, Authority-set + Audience-blank is a
/// misconfiguration (true); a configured Audience, the unconfigured-Authority fail-closed path, and any
/// non-Production environment are all allowed (false). The end-to-end "the host refuses to start" wiring is
/// covered over the real <c>Program</c> by the smoke suite.
/// </summary>
public class OidcAuthenticationExtensionsTests
{
    private const string _authority = "https://issuer.example.com";
    private const string _audience = "livecore-api";

    [Fact]
    public void IsMissingProductionAudience_is_true_for_authority_set_and_audience_blank_in_production()
    {
        Assert.True(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, null, isProductionEnvironment: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMissingProductionAudience_treats_a_null_or_whitespace_audience_as_blank_in_production(string? audience)
    {
        Assert.True(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, audience, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_when_a_valid_audience_is_configured_in_production()
    {
        // The acceptance criterion's "valid audience accepted": a configured Audience is not a
        // misconfiguration, so the host starts and audience scoping is enabled.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, _audience, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_when_no_authority_is_configured_in_production()
    {
        // No Authority => the fail-closed default handler path, where no token is ever accepted, so a
        // blank Audience cannot widen access. The unconfigured case keeps its existing behavior.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(null, null, isProductionEnvironment: true));
    }

    [Fact]
    public void IsMissingProductionAudience_is_false_for_authority_set_and_audience_blank_outside_production()
    {
        // Outside Production a blank Audience stays tolerated (the same local-development latitude
        // RequireHttpsMetadata=false allows), so a dev run against a local Keycloak still starts.
        Assert.False(OidcAuthenticationExtensions.IsMissingProductionAudience(_authority, null, isProductionEnvironment: false));
    }
}
