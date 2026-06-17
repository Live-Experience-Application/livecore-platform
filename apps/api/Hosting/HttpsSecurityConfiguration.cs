// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.HttpsPolicy;

namespace LiveCore.Api.Hosting;

/// <summary>
/// App-level HSTS and HTTPS-redirection posture for the API host (CORE-SEC-005, the "Audit Integrity and
/// Security Hardening" epic). The Core API is documented to run BEHIND a TLS-terminating reverse proxy
/// (CORE-OPS-003; docs/13_SELF_HOSTING_REQUIREMENTS.md), where the public HTTPS boundary — the
/// <c>http</c>→<c>https</c> redirect and the <c>Strict-Transport-Security</c> header — lives at the edge and
/// the app sees the real client scheme through the trusted proxy's <c>X-Forwarded-Proto</c> (restored by
/// <see cref="ForwardedHeadersConfiguration"/>'s <c>UseForwardedHeaders</c>, which runs FIRST in the pipeline).
/// Before this story the host wired NEITHER <c>UseHsts</c> NOR <c>UseHttpsRedirection</c> at all, so a
/// deployment that runs WITHOUT a terminating proxy (the API terminating TLS in Kestrel directly) had no
/// app-level transport-security defense — TLS/HSTS was entirely delegated to an edge the app cannot verify
/// exists.
///
/// This wires both as ASP.NET Core's built-in middleware (<c>Microsoft.AspNetCore.HttpsPolicy</c>, part of the
/// shared framework — NO new dependency), each guarded by its own configuration toggle:
/// <list type="bullet">
///   <item><c>UseHttpsRedirection</c> redirects an insecure <c>http</c> request to <c>https</c>. Because
///   <c>UseForwardedHeaders</c> runs first, behind a trusted terminating proxy the app already sees
///   <c>Request.Scheme == "https"</c> (the proxy forwarded <c>X-Forwarded-Proto: https</c>), so the redirect
///   does NOT fire and the proxy posture is unchanged — no double-redirect, no fight with the edge. A
///   standalone deployment that sees a genuine <c>http</c> request redirects it.</item>
///   <item><c>UseHsts</c> emits the <c>Strict-Transport-Security</c> response header on a secure response,
///   instructing the browser to use <c>https</c> for the configured max-age. The framework's default excluded
///   hosts (loopback: <c>localhost</c>/<c>127.0.0.1</c>/<c>[::1]</c>) are left intact, so local development is
///   never pinned to <c>https</c>.</item>
/// </list>
///
/// Both toggles are FAIL-SAFE OFF by default (<c>HttpsSecurity:Hsts:Enabled</c> /
/// <c>HttpsSecurity:HttpsRedirection:Enabled</c>), so the documented proxy posture — where the proxy terminates
/// TLS and owns the redirect/HSTS — is the unchanged default and a deployment turns them ON only when it
/// terminates TLS in the app itself. Every value is read from configuration only (<c>HttpsSecurity:*</c>;
/// docs/13_SELF_HOSTING_REQUIREMENTS.md), and a non-positive max-age or an out-of-range redirect status falls
/// back to the documented default rather than crashing the host (the same fail-safe posture as
/// <see cref="RateLimitingConfiguration"/>).
///
/// Transport security is a coarse edge defense layered ON TOP OF the OIDC/tenant authorization every endpoint
/// already enforces server-side; it never widens authorization (docs/07_SECURITY_THREAT_MODEL.md), exactly as
/// the CORS allow-list and the rate limiter are. The header and the redirect carry no tenant/principal/resource
/// detail (threat T7).
/// </summary>
public static class HttpsSecurityConfiguration
{
    /// <summary>Configuration section that carries the transport-security settings (<c>HttpsSecurity</c>).</summary>
    public const string ConfigurationSection = "HttpsSecurity";

    /// <summary>
    /// Default HSTS <c>max-age</c>, in days (365). A one-year max-age is the conventional production value and the
    /// floor for HSTS preload-list eligibility; left configurable so a deployment can ramp it up cautiously.
    /// </summary>
    public const int DefaultHstsMaxAgeDays = 365;

    /// <summary>
    /// Default HTTPS-redirection status code (<c>308 Permanent Redirect</c>). A permanent redirect lets a client
    /// cache the <c>http</c>→<c>https</c> upgrade; a deployment can configure <c>307</c> (temporary) instead.
    /// </summary>
    public const int DefaultHttpsRedirectionStatusCode = StatusCodes.Status308PermanentRedirect;

    /// <summary>
    /// The resolved transport-security settings. Exposed (with <see cref="Read"/>) so the wiring is testable
    /// against the exact values the host runs with, exactly like <see cref="RateLimitingConfiguration.RateLimitingSettings"/>
    /// and <see cref="ForwardedHeadersConfiguration.Apply"/>.
    /// </summary>
    public sealed record HttpsSecuritySettings(
        bool HstsEnabled,
        int HstsMaxAgeDays,
        bool HstsIncludeSubDomains,
        bool HstsPreload,
        bool HttpsRedirectionEnabled,
        int HttpsRedirectionStatusCode,
        int? HttpsRedirectionPort)
    {
        /// <summary>
        /// Reads the transport-security settings from <see cref="ConfigurationSection"/>. Both features are
        /// DISABLED by default (the documented proxy posture is unchanged); when enabled, the HSTS max-age
        /// defaults to <see cref="DefaultHstsMaxAgeDays"/> and the redirect status to
        /// <see cref="DefaultHttpsRedirectionStatusCode"/>. A non-positive max-age, a redirect status outside the
        /// <c>3xx</c> range, or an out-of-range redirect port falls back to the safe default (a misconfiguration
        /// never crashes the limiter-equivalent middleware or produces a degenerate redirect).
        /// </summary>
        /// <exception cref="ArgumentNullException">The configuration is null.</exception>
        public static HttpsSecuritySettings Read(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(ConfigurationSection);

            return new HttpsSecuritySettings(
                HstsEnabled: section.GetValue("Hsts:Enabled", false),
                HstsMaxAgeDays: PositiveOrDefault(section.GetValue<int?>("Hsts:MaxAgeDays"), DefaultHstsMaxAgeDays),
                HstsIncludeSubDomains: section.GetValue("Hsts:IncludeSubDomains", false),
                HstsPreload: section.GetValue("Hsts:Preload", false),
                HttpsRedirectionEnabled: section.GetValue("HttpsRedirection:Enabled", false),
                HttpsRedirectionStatusCode: RedirectStatusOrDefault(
                    section.GetValue<int?>("HttpsRedirection:StatusCode"), DefaultHttpsRedirectionStatusCode),
                HttpsRedirectionPort: PortOrNull(section.GetValue<int?>("HttpsRedirection:Port")));
        }

        private static int PositiveOrDefault(int? value, int fallback)
            => value is { } v && v > 0 ? v : fallback;

        private static int RedirectStatusOrDefault(int? value, int fallback)
            => value is { } v && v is >= 300 and <= 399 ? v : fallback;

        private static int? PortOrNull(int? value)
            => value is { } v && v is >= 1 and <= 65535 ? v : null;
    }

    /// <summary>
    /// Registers the framework's <see cref="HstsOptions"/> and <see cref="HttpsRedirectionOptions"/> configured
    /// from <see cref="ConfigurationSection"/>, plus the resolved <see cref="HttpsSecuritySettings"/> so the
    /// pipeline (and tests) can decide whether to wire <c>UseHsts</c>/<c>UseHttpsRedirection</c>. The options are
    /// always configured (whether or not the toggles are on); the middleware is added to the pipeline only when
    /// enabled, exactly like the rate limiter is always registered but inert when disabled. The settings are read
    /// LAZILY from the fully-assembled configuration (the same pattern <see cref="RateLimitingConfiguration"/> and
    /// <see cref="CorsConfiguration"/> use), so a source added after registration is reflected.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreHttpsSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(sp => HttpsSecuritySettings.Read(sp.GetRequiredService<IConfiguration>()));

        services.AddHsts(options =>
        {
            var settings = HttpsSecuritySettings.Read(configuration);
            options.MaxAge = TimeSpan.FromDays(settings.HstsMaxAgeDays);
            options.IncludeSubDomains = settings.HstsIncludeSubDomains;
            options.Preload = settings.HstsPreload;
            // The framework's default excluded hosts (loopback) are intentionally left in place so a
            // local/dev request is never pinned to https; a standalone deployment serving a real public host
            // still gets the header.
        });

        services.AddHttpsRedirection(options =>
        {
            var settings = HttpsSecuritySettings.Read(configuration);
            options.RedirectStatusCode = settings.HttpsRedirectionStatusCode;
            if (settings.HttpsRedirectionPort is { } port)
            {
                // Pin the redirect target port. When unset the framework resolves it from the
                // HTTPS_PORT/ASPNETCORE_HTTPS_PORT configuration or the server's HTTPS address, and if none can
                // be determined it logs and passes the request through rather than redirecting to an unknown port.
                options.HttpsPort = port;
            }
        });

        return services;
    }
}
