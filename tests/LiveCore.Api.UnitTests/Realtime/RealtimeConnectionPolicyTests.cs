using System.Security.Claims;
using LiveCore.Api.IdentityAccess;
using LiveCore.Api.Realtime;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeConnectionPolicy"/> (CORE-RT-001) — the fail-closed admission
/// decision the session hub applies on connect. The decision delegates to the same
/// <see cref="OidcPrincipalMapper"/> the REST endpoints use, so a connection is accepted only when its
/// principal normalizes to a valid OIDC principal; a null, unauthenticated or malformed principal is
/// refused (threat T5 in docs/07_SECURITY_THREAT_MODEL.md). All fixtures are generic (AGENTS.md).
/// </summary>
public sealed class RealtimeConnectionPolicyTests
{
    private const string _issuer = "https://issuer.test";
    private const string _subject = "host-a";

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "Bearer"));

    [Fact]
    public void A_valid_authenticated_principal_is_accepted()
    {
        var user = Authenticated(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject));

        Assert.True(RealtimeConnectionPolicy.IsAuthenticatedConnection(user));
    }

    [Fact]
    public void A_null_principal_is_refused()
        => Assert.False(RealtimeConnectionPolicy.IsAuthenticatedConnection(null));

    [Fact]
    public void An_unauthenticated_principal_is_refused()
    {
        // No authentication type => ClaimsIdentity.IsAuthenticated is false, so the fail-closed mapper
        // rejects it and the connection is refused.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject),
        ]));

        Assert.False(RealtimeConnectionPolicy.IsAuthenticatedConnection(user));
    }

    [Fact]
    public void A_principal_with_conflicting_subjects_is_refused()
    {
        // A token validated by the bearer pipeline but carrying ambiguous identity claims must not hold
        // an open hub connection — defence in depth beyond [Authorize].
        var user = Authenticated(
            new Claim(OidcClaimTypes.Issuer, _issuer),
            new Claim(OidcClaimTypes.Subject, _subject),
            new Claim(OidcClaimTypes.Subject, "someone-else"));

        Assert.False(RealtimeConnectionPolicy.IsAuthenticatedConnection(user));
    }

    [Fact]
    public void A_principal_missing_the_subject_is_refused()
    {
        var user = Authenticated(new Claim(OidcClaimTypes.Issuer, _issuer));

        Assert.False(RealtimeConnectionPolicy.IsAuthenticatedConnection(user));
    }
}
