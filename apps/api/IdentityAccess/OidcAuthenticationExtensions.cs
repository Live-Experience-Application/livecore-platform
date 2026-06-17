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
///
/// Production audience guard (CORE-OPS-004): audience validation below is enabled
/// only when an Audience is configured, so a blank Audience silently disables
/// audience scoping — any token the Authority's issuer signs (including one minted
/// for a different client/application of the same issuer) would be accepted. In a
/// Production environment a configured Authority with a blank Audience is treated as a
/// misconfiguration and the host REFUSES TO START
/// (<see cref="IsMissingProductionAudience"/>), rather than serving a single request
/// with audience validation off (threats T1/T5 in
/// docs/07_SECURITY_THREAT_MODEL.md). Outside Production a blank Audience stays
/// tolerated (the same local-development latitude
/// <c>RequireHttpsMetadata=false</c> allows), and the unconfigured-Authority case
/// keeps its fail-closed handler unchanged.
///
/// Bounded backchannel (CORE-RES-005): when an Authority is configured the handler
/// fetches the provider's discovery document and signing keys over HTTP. Those
/// outbound calls are bounded by a short, configurable <c>BackchannelTimeout</c>
/// plus tuned configuration-refresh intervals (<see cref="OidcBackchannelOptions"/>),
/// so a slow or unreachable provider FAILS FAST rather than stalling token validation
/// (which, with the global problem-details handler CORE-RES-001, would surface as a
/// 500). The bound never relaxes validation — a token whose key cannot be fetched in
/// time is still rejected, fail-closed.
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
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

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

        // Production audience guard (CORE-OPS-004): an Authority IS configured, so the
        // bearer pipeline below will accept tokens. ValidateAudience is enabled only
        // when an Audience is configured, so a blank Audience silently disables
        // audience scoping — any token the Authority's issuer signs (including one
        // minted for a different client/application of the same issuer) would be
        // accepted. In a Production environment that is a misconfiguration, not a
        // self-hosting choice: fail loud at startup so the host never serves a single
        // request with audience validation off (threats T1/T5). Outside Production a
        // blank Audience stays tolerated, the same dev-only latitude
        // RequireHttpsMetadata allows.
        if (IsMissingProductionAudience(authority, audience, environment.IsProduction()))
        {
            throw new InvalidOperationException(
                $"OIDC is misconfigured: an Authority is configured under '{ConfigurationSection}:Authority' " +
                $"but no Audience is configured under '{ConfigurationSection}:Audience'. A blank Audience " +
                "disables audience validation, so any token the configured issuer signs would be accepted. " +
                "Configure the API's expected token audience (the 'aud' claim) for a production deployment.");
        }

        // HTTPS metadata is required by default (production). A dev-only override
        // (RequireHttpsMetadata=false) is allowed via configuration so a
        // self-hosted http Keycloak works locally; it is never hardcoded.
        var requireHttpsMetadata = section.GetValue("RequireHttpsMetadata", true);

        // Bound the OIDC backchannel (CORE-RES-005): an Authority IS configured, so the handler will fetch the
        // provider's discovery document and signing keys over HTTP to validate tokens. Read the short, safe
        // timeout and tuned configuration-refresh intervals once (a present-but-malformed value is rejected at
        // startup), so a slow or unreachable provider fails fast rather than stalling token validation (which,
        // with CORE-RES-001, would otherwise surface as a 500).
        var backchannel = OidcBackchannelOptions.FromConfiguration(section);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.RequireHttpsMetadata = requireHttpsMetadata;

                // Outbound-call bounds (CORE-RES-005). BackchannelTimeout caps the per-request wait on the
                // backchannel HttpClient the handler uses to fetch the discovery/JWKS document, so an
                // unreachable provider fails fast. The refresh intervals tune how the cached configuration is
                // kept fresh: AutomaticRefreshInterval picks up the provider's key rotation within a bounded
                // window, and RefreshInterval is the floor that keeps a burst of validation failures from
                // hammering the metadata endpoint. The framework applies these to the default backchannel and
                // configuration manager it creates from the Authority below. None of this relaxes validation:
                // a token whose key cannot be fetched in time is still rejected (fail-closed; threats T1/T5).
                options.BackchannelTimeout = backchannel.BackchannelTimeout;
                options.AutomaticRefreshInterval = backchannel.AutomaticRefreshInterval;
                options.RefreshInterval = backchannel.RefreshInterval;

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

                    // Validate the audience whenever one is configured. A blank
                    // Audience disables the audience check (self-hosting latitude,
                    // docs/13_SELF_HOSTING_REQUIREMENTS.md) — but the production guard
                    // above has already refused to start if that happens in a
                    // Production environment (CORE-OPS-004), so a live production host
                    // always reaches here with a non-blank Audience and audience
                    // scoping enabled.
                    ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                    ValidAudience = audience,
                };

                // SignalR hub authentication (CORE-RT-001). Browser WebSocket clients cannot set the
                // Authorization header, so the SignalR client sends the access token as a query-string
                // parameter on the hub connection. Read it from there for hub paths ONLY (HubBearerToken
                // restricts query-string tokens to the /hubs area; the REST API keeps using the
                // Authorization header), so a token never leaks from a non-hub URL (threat T7). The token
                // is still fully validated by the same bearer pipeline above — this only changes where it
                // is read from, never whether it is verified.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (HubBearerToken.IsHubPath(context.HttpContext.Request.Path.Value))
                        {
                            var queryToken = context.Request.Query[HubBearerToken.QueryParameter].ToString();
                            if (!string.IsNullOrWhiteSpace(queryToken))
                            {
                                context.Token = queryToken;
                            }
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();

        return true;
    }

    /// <summary>
    /// Decides whether the OIDC configuration is the Production audience-scoping
    /// foot-gun (CORE-OPS-004): an Authority IS configured (so tokens are accepted)
    /// while the Audience is blank (so audience validation is silently disabled), in a
    /// Production environment. This is the single decision the startup guard in
    /// <see cref="AddOidcAuthentication"/> enforces; it is pure (no services, no
    /// configuration side effects) so the behavior is unit-testable directly.
    /// </summary>
    /// <param name="authority">The configured OIDC Authority, or null/blank when none is set.</param>
    /// <param name="audience">The configured OIDC Audience, or null/blank when none is set.</param>
    /// <param name="isProductionEnvironment">
    /// Whether the host runs in a Production environment
    /// (<see cref="HostEnvironmentEnvExtensions.IsProduction"/>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when, in Production, an Authority is configured but
    /// the Audience is blank. Returns <see langword="false"/> when no Authority is
    /// configured (the fail-closed handler path, where no token is ever accepted),
    /// when an Audience is configured (audience scoping is enabled), or outside
    /// Production (local-development latitude).
    /// </returns>
    public static bool IsMissingProductionAudience(
        string? authority,
        string? audience,
        bool isProductionEnvironment)
        => isProductionEnvironment
            && !string.IsNullOrWhiteSpace(authority)
            && string.IsNullOrWhiteSpace(audience);
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
