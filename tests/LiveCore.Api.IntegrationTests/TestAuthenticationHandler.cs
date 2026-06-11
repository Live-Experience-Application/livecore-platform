using System.Security.Claims;
using System.Text.Encodings.Web;
using LiveCore.Api.IdentityAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveCore.Api.IntegrationTests;

/// <summary>
/// Test authentication handler (CORE-WS-003 integration tests).
///
/// It simulates an authenticated OIDC caller WITHOUT a real identity provider by
/// reading the caller's chosen claims from request headers and building a
/// <see cref="ClaimsPrincipal"/> with the RAW OIDC claim names
/// (<c>iss</c>, <c>sub</c>, <c>organization</c>, …). This mirrors exactly what
/// the production JWT bearer handler produces with <c>MapInboundClaims = false</c>
/// (CORE-ID-001 carry-over requirement), so <see cref="OidcPrincipalMapper"/> is
/// exercised unchanged. Production authentication is NOT weakened: this handler
/// exists only in the test project and is registered only by the test factory.
///
/// Headers consumed (all optional; absence means "no token" -> the handler
/// returns NoResult so the request is treated as unauthenticated and challenged
/// as 401):
/// <list type="bullet">
///   <item><c>X-Test-Sub</c> — the OIDC subject (presence marks an authenticated
///   caller).</item>
///   <item><c>X-Test-Iss</c> — the OIDC issuer (defaults to a valid https issuer
///   when the subject is present but the issuer header is omitted).</item>
///   <item><c>X-Test-Org</c> — comma-separated organization claim values (the
///   token's asserted tenants).</item>
/// </list>
/// </summary>
internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public const string SubjectHeader = "X-Test-Sub";
    public const string IssuerHeader = "X-Test-Iss";
    public const string OrganizationHeader = "X-Test-Org";

    public const string DefaultIssuer = "https://issuer.test";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No subject header => the caller presented no token. Treat as
        // unauthenticated so RequireAuthorization challenges with 401, exactly
        // like a missing/invalid bearer token in production.
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subjectValues)
            || string.IsNullOrWhiteSpace(subjectValues.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var subject = subjectValues.ToString();
        var issuer = Request.Headers.TryGetValue(IssuerHeader, out var issuerValues)
            && !string.IsNullOrWhiteSpace(issuerValues.ToString())
                ? issuerValues.ToString()
                : DefaultIssuer;

        // Raw OIDC claim names, mirroring MapInboundClaims = false in production.
        var claims = new List<Claim>
        {
            new(OidcClaimTypes.Issuer, issuer),
            new(OidcClaimTypes.Subject, subject),
        };

        if (Request.Headers.TryGetValue(OrganizationHeader, out var organizationValues))
        {
            foreach (var organizationClaim in organizationValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(OidcClaimTypes.Organization, organizationClaim));
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
