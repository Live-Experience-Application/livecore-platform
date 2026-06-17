// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Hosting;

/// <summary>
/// Baseline HTTP security response headers for the API host (CORE-SEC-009, the "Audit Integrity and Security
/// Hardening" epic). Before this story the pipeline (apps/api/Program.cs) wired NO response-header middleware —
/// only Kestrel's <c>AddServerHeader = false</c> — so every JSON and Problem Details response was served with no
/// anti-sniffing or framing defense. CORE-SEC-005 added the transport-security headers (HSTS / HTTPS redirect);
/// this complements it with the content-handling baseline.
///
/// <para>
/// The API returns only <c>application/json</c> and <c>application/problem+json</c> and NEVER renders HTML, so the
/// baseline is deliberately conservative — a deny-all posture rather than a script/style allow-list:
/// <list type="bullet">
///   <item><c>X-Content-Type-Options: nosniff</c> stops a browser MIME-sniffing a response into a type the
///   server did not declare.</item>
///   <item><c>Referrer-Policy: no-referrer</c> keeps the request URL (which can carry a resource id) out of the
///   <c>Referer</c> of any onward navigation.</item>
///   <item><c>Content-Security-Policy: default-src 'none'; frame-ancestors 'none'</c> — a deny-all CSP. The API
///   serves no HTML, scripts or styles, so no source needs allowing; <c>frame-ancestors 'none'</c> forbids
///   embedding the responses in a frame, which subsumes <c>X-Frame-Options</c> (so no separate header is
///   added).</item>
/// </list>
/// </para>
///
/// <para>
/// The whole feature is ON BY DEFAULT (<c>SecurityHeaders:Enabled</c>) and each header is INDIVIDUALLY
/// configurable: an operator can turn one header off (<c>SecurityHeaders:&lt;Header&gt;:Enabled=false</c> removes
/// exactly that header) or override its value (<c>SecurityHeaders:&lt;Header&gt;:Value</c>) while the others are
/// untouched. A blank/whitespace configured value falls back to the documented default rather than emitting an
/// empty header, the same fail-safe posture <see cref="HttpsSecurityConfiguration"/> and
/// <see cref="RateLimitingConfiguration"/> take.
/// </para>
///
/// <para>
/// The headers are STATIC: every value is a fixed directive string, never a tenant, principal, resource or
/// request value, so they leak no tenant/principal detail (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). Like
/// the CORS allow-list, the rate limiter and transport security, they are a coarse browser-facing defense layered
/// ON TOP OF the OIDC/tenant authorization every endpoint already enforces server-side; they never widen
/// authorization.
/// </para>
/// </summary>
public static class SecurityHeadersConfiguration
{
    /// <summary>Configuration section that carries the security-header settings (<c>SecurityHeaders</c>).</summary>
    public const string ConfigurationSection = "SecurityHeaders";

    /// <summary>The <c>X-Content-Type-Options</c> header name.</summary>
    public const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";

    /// <summary>The <c>Referrer-Policy</c> header name.</summary>
    public const string ReferrerPolicyHeaderName = "Referrer-Policy";

    /// <summary>The <c>Content-Security-Policy</c> header name.</summary>
    public const string ContentSecurityPolicyHeaderName = "Content-Security-Policy";

    /// <summary>Default <c>X-Content-Type-Options</c> value: stop MIME content-sniffing.</summary>
    public const string DefaultContentTypeOptions = "nosniff";

    /// <summary>Default <c>Referrer-Policy</c> value: never send a <c>Referer</c> (keeps resource ids out of it).</summary>
    public const string DefaultReferrerPolicy = "no-referrer";

    /// <summary>
    /// Default <c>Content-Security-Policy</c> value: a deny-all policy. The API renders no HTML, so no source
    /// needs allowing; <c>frame-ancestors 'none'</c> forbids framing the responses (subsuming
    /// <c>X-Frame-Options</c>).
    /// </summary>
    public const string DefaultContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The resolved security-header settings. Exposed (with <see cref="Read"/> and <see cref="Apply"/>) so the
    /// wiring is testable against the exact values the host runs with, exactly like
    /// <see cref="HttpsSecurityConfiguration.HttpsSecuritySettings"/> and
    /// <see cref="RateLimitingConfiguration.RateLimitingSettings"/>.
    /// </summary>
    public sealed record SecurityHeadersSettings(
        bool Enabled,
        bool ContentTypeOptionsEnabled,
        string ContentTypeOptions,
        bool ReferrerPolicyEnabled,
        string ReferrerPolicy,
        bool ContentSecurityPolicyEnabled,
        string ContentSecurityPolicy)
    {
        /// <summary>
        /// Reads the security-header settings from <see cref="ConfigurationSection"/>. The feature and every header
        /// default to ENABLED (the baseline is on out of the box), and every value defaults to the documented safe
        /// directive (<see cref="DefaultContentTypeOptions"/> / <see cref="DefaultReferrerPolicy"/> /
        /// <see cref="DefaultContentSecurityPolicy"/>). A blank/whitespace configured value falls back to its
        /// default rather than emitting an empty header.
        /// </summary>
        /// <exception cref="ArgumentNullException">The configuration is null.</exception>
        public static SecurityHeadersSettings Read(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(ConfigurationSection);

            return new SecurityHeadersSettings(
                Enabled: section.GetValue("Enabled", true),
                ContentTypeOptionsEnabled: section.GetValue("ContentTypeOptions:Enabled", true),
                ContentTypeOptions: NonEmptyOrDefault(section["ContentTypeOptions:Value"], DefaultContentTypeOptions),
                ReferrerPolicyEnabled: section.GetValue("ReferrerPolicy:Enabled", true),
                ReferrerPolicy: NonEmptyOrDefault(section["ReferrerPolicy:Value"], DefaultReferrerPolicy),
                ContentSecurityPolicyEnabled: section.GetValue("ContentSecurityPolicy:Enabled", true),
                ContentSecurityPolicy: NonEmptyOrDefault(
                    section["ContentSecurityPolicy:Value"], DefaultContentSecurityPolicy));
        }

        /// <summary>
        /// Writes the ENABLED baseline headers onto <paramref name="headers"/>, each set to its configured value
        /// (replacing any existing value so the baseline is exact). A header whose individual toggle is off is not
        /// written, so turning a header off via configuration removes exactly that header and leaves the others.
        /// Called from <see cref="SecurityHeadersMiddleware"/> via <c>HttpResponse.OnStarting</c>, so the headers
        /// ride on EVERY response — the success path and an error/Problem Details path (e.g. a 404/406/500) alike,
        /// even one whose body was written after the pipeline cleared the response.
        /// </summary>
        /// <exception cref="ArgumentNullException">The headers dictionary is null.</exception>
        public void Apply(IHeaderDictionary headers)
        {
            ArgumentNullException.ThrowIfNull(headers);

            if (ContentTypeOptionsEnabled)
            {
                headers[ContentTypeOptionsHeaderName] = ContentTypeOptions;
            }

            if (ReferrerPolicyEnabled)
            {
                headers[ReferrerPolicyHeaderName] = ReferrerPolicy;
            }

            if (ContentSecurityPolicyEnabled)
            {
                headers[ContentSecurityPolicyHeaderName] = ContentSecurityPolicy;
            }
        }

        private static string NonEmptyOrDefault(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Registers the resolved <see cref="SecurityHeadersSettings"/> so the pipeline's
    /// <see cref="SecurityHeadersMiddleware"/> (and tests) run with the exact values the host is configured with.
    /// The settings are read LAZILY from the fully-assembled configuration (the same pattern
    /// <see cref="HttpsSecurityConfiguration"/>, <see cref="RateLimitingConfiguration"/> and
    /// <see cref="CorsConfiguration"/> use), so a configuration source added after registration is reflected.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreSecurityHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(sp => SecurityHeadersSettings.Read(sp.GetRequiredService<IConfiguration>()));

        return services;
    }
}
