// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace LiveCore.Api.Hosting;

/// <summary>
/// The production secret-management and configuration contract (CORE-OPS-008, the "Production Operations
/// Readiness" epic).
///
/// Every secret/config value the host consumes is supplied at runtime from configuration only — no credential
/// lives in source (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The full, names-only contract ships as the
/// repository-root <c>.env.example</c>, and the env-var → secret-store mapping is documented in
/// docs/13_SELF_HOSTING_REQUIREMENTS.md. This type is the single, testable list of the settings that MUST be
/// present for a correct production deployment, plus the pure decision that reports which of them are missing.
///
/// It is the startup-log counterpart of the readiness gate and the audience guard, reusing the same
/// environment-aware, fail-closed posture rather than adding a new one:
/// <list type="bullet">
/// <item>Outside Production the contract is inert (an empty missing-list), preserving the local-development
/// latitude the host already grants — a Development run with no database or identity provider still starts
/// (the same latitude as <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/>,
/// CORE-OPS-004, and <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/>, CORE-OPS-005).</item>
/// <item>In Production a missing required value does NOT crash an otherwise-live process: the host stays up and
/// fails closed (authenticated routes 401, persistence-backed routes 503) and reports NOT-READY (CORE-OPS-005),
/// while <see cref="Program"/> logs a loud, NAMED startup error so the misconfiguration is unmissable. The one
/// hard fail-to-start case is the security foot-gun where an Authority is configured but the Audience is blank
/// (audience validation silently disabled), which the OIDC audience guard refuses at startup (CORE-OPS-004).</item>
/// </list>
///
/// The decision reports only the missing setting KEY NAMES, never any configured value, so it can be logged
/// without leaking a secret (threat T7) — the same reason the readiness response stays status-only.
///
/// <para>
/// WELL-FORMEDNESS (CORE-OPS-013, the "Deployment and Operations Completeness" epic). Before this story the
/// contract checked only that the required keys are non-BLANK, never that the values PARSE — so a present-but-
/// malformed connection string, Authority, CORS origin or trusted-network CIDR was discovered only at the first
/// request (or silently misbehaved), inconsistent with <see cref="GracefulShutdownOptions"/> and the worker
/// options, which reject a malformed value loudly at startup. <see cref="FindMalformedCriticalSettings"/> closes
/// that gap for the critical values the host can validate WITHOUT any I/O — the connection string parses, the
/// Authority is an absolute http(s) URI, each <c>Cors:AllowedOrigins</c> entry is scheme+host[+port] with no
/// path/wildcard, and each <c>ForwardedHeaders:KnownNetworks</c> entry is a parseable CIDR — and a malformed
/// value emits the SAME loud, named <c>Critical</c> log (<see cref="Program"/>) and not-ready posture
/// (<see cref="AddMalformedConfigurationReadinessCheck"/>) as a missing one. Like the missing-value contract it
/// reports only the KEY NAMES, never the configured value (threat T7), and is inert outside Production
/// (local-development latitude).
/// </para>
/// </summary>
public static class ProductionConfigurationValidator
{
    /// <summary>
    /// Configuration key of the PostgreSQL connection string (<c>ConnectionStrings__Database</c>). It carries
    /// the database password, so it is a secret and is injected from the deployment's secret store only
    /// (docs/13_SELF_HOSTING_REQUIREMENTS.md).
    /// </summary>
    public const string DatabaseConnectionStringKey = "ConnectionStrings:Database";

    /// <summary>
    /// Configuration key of the OIDC issuer/authority the API validates access tokens against
    /// (<c>Authentication__Oidc__Authority</c>). Not a secret, but required in production: with no Authority
    /// every authenticated endpoint fails closed with 401 (docs/adr/0005-oidc-first-authentication.md).
    /// </summary>
    public const string OidcAuthorityKey = "Authentication:Oidc:Authority";

    /// <summary>
    /// Configuration key of the expected token audience (<c>Authentication__Oidc__Audience</c>, the <c>aud</c>
    /// claim). Required in production: a blank Audience disables audience scoping, which the audience guard
    /// refuses at startup once an Authority is configured (CORE-OPS-004).
    /// </summary>
    public const string OidcAudienceKey = "Authentication:Oidc:Audience";

    /// <summary>
    /// The configuration keys that MUST be present for a correct production deployment. These are the always-
    /// required values; the conditionally-required secret groups (object storage <c>Assets:Storage:*</c>, the
    /// realtime backplane <c>Realtime:Backplane:*</c>, the store verification/notification adapter credentials)
    /// are required only for the features that use them and stay fail-closed when unset, so they are documented
    /// in the contract (<c>.env.example</c>, docs/13) rather than hard-required here.
    /// </summary>
    public static IReadOnlyList<string> RequiredProductionSettings { get; } =
    [
        DatabaseConnectionStringKey,
        OidcAuthorityKey,
        OidcAudienceKey,
    ];

    /// <summary>
    /// The single, pure decision behind the startup configuration contract: in a Production environment, which
    /// of the <see cref="RequiredProductionSettings"/> are absent (null or whitespace). Pure (no services, no
    /// configuration side effects) so the behavior is unit-testable directly, mirroring
    /// <see cref="IdentityAccess.OidcAuthenticationExtensions.IsMissingProductionAudience"/> (CORE-OPS-004) and
    /// <see cref="RequiredDependencyReadiness.RequiredDependencyMissing"/> (CORE-OPS-005).
    /// </summary>
    /// <param name="configuration">The host configuration the required values are read from.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <returns>
    /// The required setting keys that are missing, in declared order. Empty outside Production (the contract is
    /// inert, preserving local-development latitude) and empty in Production when every required value is
    /// present. Only the KEY NAMES are returned, never the configured values, so the result can be logged
    /// without leaking a secret (threat T7).
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> FindMissingRequiredSettings(
        IConfiguration configuration,
        bool isProductionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Outside Production the contract is inert, the same local-development latitude the OIDC audience guard
        // (CORE-OPS-004) and the readiness gate (CORE-OPS-005) grant: a dev run without these values still
        // starts and fails closed.
        if (!isProductionEnvironment)
        {
            return [];
        }

        var missing = new List<string>();
        foreach (var key in RequiredProductionSettings)
        {
            if (string.IsNullOrWhiteSpace(configuration[key]))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    /// <summary>
    /// Configuration key of the browser/PWA CORS allow-list (<c>Cors:AllowedOrigins</c>, CORE-OPS-003). Each
    /// entry must be a scheme+host[+port] origin with no path or wildcard; a malformed entry is reported by this
    /// key name (CORE-OPS-013).
    /// </summary>
    public const string CorsAllowedOriginsKey =
        CorsConfiguration.ConfigurationSection + ":" + CorsConfiguration.AllowedOriginsKey;

    /// <summary>
    /// Configuration key of the trusted reverse-proxy networks (<c>ForwardedHeaders:KnownNetworks</c>,
    /// CORE-OPS-003). Each entry must be a parseable CIDR; a malformed entry is reported by this key name
    /// (CORE-OPS-013).
    /// </summary>
    public const string ForwardedHeadersKnownNetworksKey =
        ForwardedHeadersConfiguration.ConfigurationSection + ":" + ForwardedHeadersConfiguration.KnownNetworksKey;

    /// <summary>
    /// Name the malformed-configuration readiness check is registered under. Kept server-side (the readiness
    /// endpoint writes only the overall status), never leaked to the unauthenticated response (threat T7).
    /// </summary>
    public const string MalformedConfigurationCheckName = "configuration-well-formed";

    /// <summary>
    /// The pure decision behind the startup well-formedness contract (CORE-OPS-013): in a Production environment,
    /// which of the critical values that can be validated WITHOUT I/O are PRESENT but malformed. Pure (no
    /// services, no configuration side effects) so the behavior is unit-testable directly, exactly like
    /// <see cref="FindMissingRequiredSettings"/>.
    ///
    /// <para>
    /// A value is checked only when it is PRESENT (non-blank): an absent value is the missing-value contract's
    /// concern (<see cref="FindMissingRequiredSettings"/>), so the two diagnostics never double-report the same
    /// key. The checks are exactly those the host can decide locally with no network call:
    /// </para>
    /// <list type="bullet">
    /// <item><c>ConnectionStrings:Database</c> — parses as a PostgreSQL connection string.</item>
    /// <item><c>Authentication:Oidc:Authority</c> — is an absolute http(s) URI (the issuer the OIDC handler
    /// appends <c>/.well-known/openid-configuration</c> to).</item>
    /// <item><c>Cors:AllowedOrigins</c> — every entry is a scheme+host[+port] origin with no userinfo, path,
    /// query, fragment or wildcard.</item>
    /// <item><c>ForwardedHeaders:KnownNetworks</c> — every entry is a parseable CIDR network.</item>
    /// </list>
    /// </summary>
    /// <param name="configuration">The host configuration the critical values are read from.</param>
    /// <param name="isProductionEnvironment">Whether the host runs in a Production environment.</param>
    /// <returns>
    /// The malformed critical setting keys, in declared order. Empty outside Production (the contract is inert,
    /// preserving local-development latitude) and empty in Production when every present critical value is
    /// well-formed. Only the KEY NAMES are returned, never the configured values, so the result can be logged
    /// without leaking a secret (threat T7).
    /// </returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static IReadOnlyList<string> FindMalformedCriticalSettings(
        IConfiguration configuration,
        bool isProductionEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Outside Production the contract is inert, the same local-development latitude the missing-value contract
        // grants: a dev run with a half-finished value still starts and fails closed.
        if (!isProductionEnvironment)
        {
            return [];
        }

        var malformed = new List<string>();

        var connectionString = configuration[DatabaseConnectionStringKey];
        if (!string.IsNullOrWhiteSpace(connectionString) && !IsParseableConnectionString(connectionString))
        {
            malformed.Add(DatabaseConnectionStringKey);
        }

        var authority = configuration[OidcAuthorityKey];
        if (!string.IsNullOrWhiteSpace(authority) && !IsAbsoluteHttpUri(authority))
        {
            malformed.Add(OidcAuthorityKey);
        }

        if (HasMalformedCorsOrigin(configuration))
        {
            malformed.Add(CorsAllowedOriginsKey);
        }

        if (HasMalformedKnownNetwork(configuration))
        {
            malformed.Add(ForwardedHeadersKnownNetworksKey);
        }

        return malformed;
    }

    /// <summary>
    /// Registers the malformed-configuration readiness check (tagged <see cref="HealthEndpoints.ReadinessTag"/>)
    /// so <c>/health/ready</c> reports NOT-READY in Production when any critical value is present but malformed —
    /// the same not-ready posture a MISSING required value already produces (CORE-OPS-005). Configuration is fixed
    /// at startup, so the well-formedness verdict is captured here once (exactly like
    /// <see cref="RequiredDependencyReadiness.AddRequiredDependencyReadinessCheck"/> captures the configured/
    /// unconfigured booleans) rather than re-evaluated on every probe. Registered UNCONDITIONALLY (independent of
    /// whether persistence is configured) so it runs even when persistence is absent, and the readiness response
    /// stays status-only so WHICH key is malformed never leaks (threat T7).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration the critical values are read from.</param>
    /// <param name="environment">The host environment (the check is environment-aware: inert outside Production).</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static IServiceCollection AddMalformedConfigurationReadinessCheck(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var hasMalformedConfiguration =
            FindMalformedCriticalSettings(configuration, environment.IsProduction()).Count > 0;

        services.AddHealthChecks().AddCheck(
            MalformedConfigurationCheckName,
            new MalformedConfigurationHealthCheck(hasMalformedConfiguration),
            tags: [HealthEndpoints.ReadinessTag]);

        return services;
    }

    private static bool IsParseableConnectionString(string value)
    {
        try
        {
            // NpgsqlConnectionStringBuilder is the same parser the persistence layer uses; constructing it rejects
            // an unparseable string, an unknown keyword or a value that cannot be coerced to its typed keyword
            // (e.g. a non-numeric Port). Any such parse failure means the connection string is malformed.
            _ = new NpgsqlConnectionStringBuilder(value);
            return true;
        }
        catch (Exception ex)
            when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsAbsoluteHttpUri(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool HasMalformedCorsOrigin(IConfiguration configuration)
    {
        // Judge well-formedness against the EXACT origins the CORS policy is built from (trimmed, single trailing
        // slash removed, blanks dropped), so this never disagrees with what the policy actually consumes.
        foreach (var origin in CorsConfiguration.ReadAllowedOrigins(configuration))
        {
            if (!IsWellFormedCorsOrigin(origin))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWellFormedCorsOrigin(string origin)
    {
        // A CORS origin is scheme+host[+port] only: an absolute http/https URI with no userinfo, path, query,
        // fragment or wildcard (ReadAllowedOrigins has already removed a single trailing slash).
        if (origin.Contains('*'))
        {
            return false;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0;
    }

    private static bool HasMalformedKnownNetwork(IConfiguration configuration)
    {
        var networks = configuration
            .GetSection(ForwardedHeadersKnownNetworksKey)
            .Get<string[]>() ?? [];

        foreach (var network in networks)
        {
            if (string.IsNullOrWhiteSpace(network))
            {
                continue;
            }

            if (!System.Net.IPNetwork.TryParse(network.Trim(), out _))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The readiness <see cref="IHealthCheck"/> behind
/// <see cref="ProductionConfigurationValidator.AddMalformedConfigurationReadinessCheck"/> (CORE-OPS-013). It
/// captures the startup-time well-formedness verdict and reports NOT-READY (Unhealthy) when a critical
/// configuration value is present but malformed, so a malformed value leaves the ready rotation exactly as a
/// missing required value does (CORE-OPS-005). The failure description stays generic and server-side (the
/// readiness endpoint writes only the overall status), so it never tells an anonymous caller WHICH key is
/// malformed (threat T7).
/// </summary>
internal sealed class MalformedConfigurationHealthCheck : IHealthCheck
{
    private readonly bool _hasMalformedConfiguration;

    public MalformedConfigurationHealthCheck(bool hasMalformedConfiguration)
        => _hasMalformedConfiguration = hasMalformedConfiguration;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_hasMalformedConfiguration
            ? HealthCheckResult.Unhealthy("A critical production configuration value is present but malformed.")
            : HealthCheckResult.Healthy());
}
