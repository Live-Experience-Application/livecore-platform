using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LiveCore.Api.IdentityAccess;

/// <summary>
/// JWT bearer authentication wiring for the OIDC-first request flow
/// (CORE-WS-003, the first endpoint story; docs/02_ARCHITECTURE.md request flow:
/// "authentication middleware -> tenant/workspace context resolver -> endpoint
/// -> authorization policy"; docs/adr/0005: OIDC-first, no custom password
/// authentication).
///
/// The host validates access tokens issued by an external OIDC provider
/// (Keycloak by default) and never implements password authentication itself.
/// All configuration is read from configuration only (the
/// <c>Authentication:Oidc:*</c> keys); no secrets live in this repository
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md: OIDC issuer/audience configuration is
/// runtime configuration; threat T7: nothing sensitive is hardcoded).
///
/// Safe absence handling: the bearer scheme is registered only when an Authority
/// is configured. Without it the host still starts (local runs and the existing
/// smoke tests need no identity provider), but a fail-closed default scheme
/// (<see cref="FailClosedAuthenticationHandler"/>) is registered so any
/// authenticated endpoint is challenged with 401 — never anonymous access, and
/// never a 500 from a missing default challenge scheme.
///
/// Critical carry-over requirement (CORE-ID-001,
/// <see cref="OidcClaimTypes"/>): inbound claim type mapping is disabled
/// (<c>MapInboundClaims = false</c>) so the raw OIDC claim names
/// (<c>iss</c>, <c>sub</c>, <c>organization</c>, …) survive intact for
/// <see cref="OidcPrincipalMapper"/>. With mapping enabled the .NET JWT handler
/// rewrites them to legacy SOAP-era URIs and the mapper — which reads the
/// original names — would fail closed on every request.
/// </summary>
public static class OidcAuthenticationExtensions
{
    /// <summary>
    /// Configuration section that carries the OIDC token-validation settings.
    /// </summary>
    public const string ConfigurationSection = "Authentication:Oidc";

    /// <summary>
    /// Adds JWT bearer authentication for the OIDC provider when an Authority is
    /// configured under <see cref="ConfigurationSection"/>. Returns
    /// <see langword="true"/> when the bearer scheme was registered, and
    /// <see langword="false"/> when no Authority is configured (the host then
    /// starts with a fail-closed default scheme, so authenticated endpoints
    /// challenge with 401). Authentication services are always added so
    /// <c>UseAuthentication()</c>/<c>UseAuthorization()</c> are valid in the
    /// pipeline either way.
    /// </summary>
    public static bool AddOidcAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var authority = section["Authority"];
        var audience = section["Audience"];

        // No Authority configured: register a fail-closed default scheme that
        // never authenticates a caller. The host still starts (local runs and the
        // smoke tests need no identity provider), but any authenticated endpoint
        // is challenged with 401 — never anonymous access, and never a 500 from a
        // missing default challenge scheme. This matters for a realistic
        // misconfiguration where persistence is provisioned but the identity
        // provider env vars are not yet set: the API denies cleanly (401) instead
        // of leaking a 500 (threat T7).
        if (string.IsNullOrWhiteSpace(authority))
        {
            services
                .AddAuthentication(FailClosedAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, FailClosedAuthenticationHandler>(
                    FailClosedAuthenticationHandler.SchemeName, null);
            services.AddAuthorization();
            return false;
        }

        // HTTPS metadata is required by default (production). A dev-only override
        // (RequireHttpsMetadata=false) is allowed via configuration so a
        // self-hosted http Keycloak works locally; it is never hardcoded.
        var requireHttpsMetadata = section.GetValue("RequireHttpsMetadata", true);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                // Preserve the raw OIDC claim names for OidcPrincipalMapper
                // (CORE-ID-001 carry-over requirement). Without this the handler
                // remaps iss/sub/etc. and the fail-closed mapper rejects every
                // token.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // The mapper reads the issuer from the token's iss claim, but
                    // the handler must still validate the signing issuer against
                    // the configured Authority's metadata.
                    ValidateIssuer = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // Validate the audience only when one is configured; an empty
                    // Audience disables the audience check rather than rejecting
                    // every token (self-hosting flexibility,
                    // docs/13_SELF_HOSTING_REQUIREMENTS.md).
                    ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                    ValidAudience = audience,
                };
            });
        services.AddAuthorization();

        return true;
    }
}

/// <summary>
/// Default authentication scheme used when no OIDC Authority is configured. It
/// never authenticates a caller, so protected endpoints challenge with 401
/// (fail-closed) instead of throwing for a missing default scheme. It exists so a
/// misconfigured deployment — persistence provisioned but the identity provider
/// not yet configured — denies cleanly with 401 rather than returning 500, and
/// never grants anonymous access (threats T1/T7 in
/// docs/07_SECURITY_THREAT_MODEL.md). The integration test host overrides the
/// default scheme, so this never affects tests.
/// </summary>
internal sealed class FailClosedAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Name of the fail-closed default scheme.</summary>
    public const string SchemeName = "FailClosed";

    // No identity provider is configured, so no caller is ever authenticated. The
    // base handler's challenge then writes a 401, which is exactly the
    // fail-closed behavior the protected endpoints need.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
