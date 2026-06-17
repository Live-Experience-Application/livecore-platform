// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.AspNetCore.ResponseCompression;

namespace LiveCore.Api.Hosting;

/// <summary>
/// HTTP response compression for the API host (CORE-PERF-006, the "Performance and Scalability" epic). Before
/// this story the pipeline (apps/api/Program.cs) wired NEITHER <c>AddResponseCompression</c> NOR
/// <c>UseResponseCompression</c>, so the list/feed/replay endpoints — which return JSON ARRAYS that can be large
/// for a busy session — were always sent uncompressed, inflating bandwidth and latency for large sessions and
/// for mobile clients on a constrained link. This wires ASP.NET Core's built-in response-compression middleware
/// (<c>Microsoft.AspNetCore.ResponseCompression</c>, part of the shared framework — NO new dependency) so a
/// client that advertises <c>Accept-Encoding: br</c> or <c>gzip</c> receives the SAME JSON, compressed.
///
/// <para>
/// The posture is deliberately conservative and changes only transfer encoding, never response CONTENT:
/// <list type="bullet">
///   <item><b>JSON only.</b> Only <c>application/json</c> and the RFC 7807 <c>application/problem+json</c> error
///   shape are compressed (<see cref="CompressibleJsonMimeTypes"/>). Every other content type the host serves —
///   the Prometheus <c>/metrics</c> text, a signed-asset redirect, any binary/already-compressed payload — is
///   left untouched, so already-compressed content is never recompressed. The middleware additionally skips any
///   response that already carries a <c>Content-Encoding</c>.</item>
///   <item><b>Brotli preferred, gzip fallback.</b> Both providers are registered (Brotli first, so it wins when
///   a client accepts both); a client that accepts neither, or sends no <c>Accept-Encoding</c>, receives the
///   response uncompressed exactly as before.</item>
///   <item><b>The SignalR hub transport is excluded</b> at the pipeline seam, not here: the middleware is added
///   only for non-hub paths (see <c>Program.cs</c>), so the <c>/hubs</c> negotiate/transport is never
///   double-compressed (SignalR manages its own transport framing).</item>
/// </list>
/// </para>
///
/// <para>
/// The feature is ON BY DEFAULT and configurable (<c>ResponseCompression:*</c>;
/// docs/13_SELF_HOSTING_REQUIREMENTS.md), the same fail-safe configuration pattern
/// <see cref="SecurityHeadersConfiguration"/> and <see cref="HttpsSecurityConfiguration"/> use:
/// <list type="bullet">
///   <item><c>ResponseCompression:Enabled</c> (default <c>true</c>) turns the whole feature off — the middleware
///   is then not added and every response is sent uncompressed.</item>
///   <item><c>ResponseCompression:EnableForHttps</c> (default <c>true</c>) compresses over HTTPS too. The API
///   serves bearer-token-authorized JSON DATA — never HTML that mixes a stable secret (a CSRF token) with
///   reflected request input — so the BREACH precondition does not apply; an operator who still wants the
///   framework-default HTTPS posture can set it to <c>false</c>. Compression carries no tenant/principal/resource
///   detail (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).</item>
/// </list>
/// Compression is a coarse bandwidth optimization layered ON TOP OF the OIDC/tenant authorization every endpoint
/// already enforces; it never changes a response body or widens authorization.
/// </para>
/// </summary>
public static class ResponseCompressionConfiguration
{
    /// <summary>Configuration section that carries the response-compression settings (<c>ResponseCompression</c>).</summary>
    public const string ConfigurationSection = "ResponseCompression";

    /// <summary>
    /// The JSON content types worth compressing: the list/feed/replay payloads (<c>application/json</c>) and the
    /// RFC 7807 Problem Details error shape (<c>application/problem+json</c>). Deliberately JSON-only — every
    /// other content type the host serves is left untouched, so binary/already-compressed/streaming content is
    /// never recompressed. The match ignores the <c>charset</c> parameter, so <c>application/json; charset=utf-8</c>
    /// (the minimal-API default) is covered.
    /// </summary>
    public static IReadOnlyList<string> CompressibleJsonMimeTypes { get; } =
    [
        "application/json",
        "application/problem+json",
    ];

    /// <summary>
    /// The resolved response-compression settings. Exposed (with <see cref="Read"/>) so the wiring is testable
    /// against the exact values the host runs with, exactly like
    /// <see cref="SecurityHeadersConfiguration.SecurityHeadersSettings"/> and
    /// <see cref="HttpsSecurityConfiguration.HttpsSecuritySettings"/>.
    /// </summary>
    public sealed record ResponseCompressionSettings(bool Enabled, bool EnableForHttps)
    {
        /// <summary>
        /// Reads the response-compression settings from <see cref="ConfigurationSection"/>. Both the feature and
        /// HTTPS compression default to ENABLED (the bandwidth optimization is on out of the box); an operator
        /// turns the feature off (<c>ResponseCompression:Enabled=false</c>) to send everything uncompressed, or
        /// drops to the framework's HTTPS-off posture (<c>ResponseCompression:EnableForHttps=false</c>).
        /// </summary>
        /// <exception cref="ArgumentNullException">The configuration is null.</exception>
        public static ResponseCompressionSettings Read(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            var section = configuration.GetSection(ConfigurationSection);

            return new ResponseCompressionSettings(
                Enabled: section.GetValue("Enabled", true),
                EnableForHttps: section.GetValue("EnableForHttps", true));
        }
    }

    /// <summary>
    /// Registers ASP.NET Core's response-compression services (the Brotli and gzip providers and the JSON-only
    /// MIME allow-list) configured from <see cref="ConfigurationSection"/>, plus the resolved
    /// <see cref="ResponseCompressionSettings"/> so the pipeline (and tests) can decide whether to add
    /// <c>UseResponseCompression</c>. The services are always registered (whether or not the feature is on); the
    /// middleware is added to the pipeline only when enabled, exactly like the rate limiter and HSTS are always
    /// registered but inert when disabled. The settings are read LAZILY from the fully-assembled configuration
    /// (the same pattern <see cref="HttpsSecurityConfiguration"/> and <see cref="SecurityHeadersConfiguration"/>
    /// use), so a configuration source added after registration is reflected.
    /// </summary>
    /// <exception cref="ArgumentNullException">The services or configuration is null.</exception>
    public static IServiceCollection AddLiveCoreResponseCompression(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(sp => ResponseCompressionSettings.Read(sp.GetRequiredService<IConfiguration>()));

        services.AddResponseCompression(options =>
        {
            var settings = ResponseCompressionSettings.Read(configuration);

            // Compress JSON over HTTPS too — list/feed/replay payloads are served over TLS to mobile clients,
            // which is exactly the bandwidth this story targets. The BREACH precondition (a stable secret mixed
            // with reflected input in an HTML response) does not apply to the bearer-token-authorized JSON data
            // the API returns; still operator-toggleable for a deployment that wants the framework default.
            options.EnableForHttps = settings.EnableForHttps;

            // JSON only — never the Prometheus /metrics text, a signed-asset redirect or any binary payload.
            options.MimeTypes = CompressibleJsonMimeTypes;

            // Brotli first so it is preferred when a client accepts both; gzip is the broad-compatibility fallback.
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });

        return services;
    }
}
