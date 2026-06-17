// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using System.Globalization;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Tunable bounds on the per-request authorization-lookup cache (CORE-PERF-003, the "Performance and Scalability"
/// epic). Every authenticated request runs the tenant context resolver (organization-by-slug, user-profile-by-OIDC
/// and organization-membership lookups) before the endpoint, plus per-endpoint workspace membership/role
/// re-queries; at a high request rate the SAME principal re-issues exactly those stable lookups on every request
/// and hub connect. The <see cref="AuthorizationLookupCache"/> serves them from a SHORT-TTL in-memory cache so the
/// queries are not re-issued within the window, while explicit invalidation on every membership change keeps
/// authorization decisions correct and fail-closed.
///
/// <para>
/// Both settings are read once from configuration (under <see cref="ConfigurationSection"/>) with safe defaults so
/// a host runs without any cache tuning, exactly like <see cref="LiveCorePersistenceOptions"/>:
/// </para>
///
/// <list type="bullet">
///   <item><b><see cref="Enabled"/></b> — whether the cache is on. ON by default. A deployment can disable it
///   (<c>AuthorizationCache:Enabled=false</c>) to force every authorization lookup straight to the database — a
///   fail-safe escape hatch that changes only throughput, never an authorization decision, because the cache only
///   ever holds POSITIVE results that explicit invalidation evicts on any membership change.</item>
///   <item><b><see cref="Ttl"/></b> — the absolute time-to-live of a cached lookup. Short by default
///   (<see cref="DefaultTtl"/>): the cache is invalidated EXPLICITLY on every membership change, so the TTL is only
///   the backstop that bounds how long an entry can outlive a change the explicit invalidation did not observe.</item>
/// </list>
///
/// <para>
/// No secret lives here (a flag and a timespan) and the values are product-neutral (AGENTS.md).
/// </para>
/// </summary>
internal sealed class AuthorizationCacheOptions
{
    /// <summary>Configuration section the authorization-cache tuning is read from (<c>AuthorizationCache</c>).</summary>
    public const string ConfigurationSection = "AuthorizationCache";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> toggling the cache on/off.</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> holding the cache entry TTL.</summary>
    public const string TtlKey = "Ttl";

    /// <summary>
    /// Default cache entry TTL (10 seconds) when none is configured. Short on purpose: explicit invalidation on
    /// every membership change is the primary correctness mechanism, and the TTL only bounds the residual window.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(10);

    /// <summary>Creates authorization-cache tuning options.</summary>
    /// <param name="enabled">Whether the cache is enabled.</param>
    /// <param name="ttl">The absolute per-entry TTL. Must be positive (a non-positive TTL would defeat caching).</param>
    /// <exception cref="ArgumentOutOfRangeException">The TTL is not positive.</exception>
    public AuthorizationCacheOptions(bool enabled, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl), ttl, "The authorization cache TTL must be positive.");
        }

        Enabled = enabled;
        Ttl = ttl;
    }

    /// <summary>Whether the per-request authorization-lookup cache is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>The absolute time-to-live of a cached authorization lookup.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>The safe defaults (enabled, <see cref="DefaultTtl"/>), used by tests and as the fallback.</summary>
    public static AuthorizationCacheOptions Default { get; } = new(true, DefaultTtl);

    /// <summary>
    /// Reads the authorization-cache options from configuration under <see cref="ConfigurationSection"/>
    /// (<c>AuthorizationCache:Enabled</c> / <c>AuthorizationCache:Ttl</c>), falling back to the safe defaults when a
    /// value is absent or blank. A value that is PRESENT but cannot be parsed (or is out of range) is rejected at
    /// startup rather than silently falling back — a misconfigured cache is a startup error, never a degenerate
    /// (or unbounded) cache.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">A present value cannot be parsed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured TTL value is out of range.</exception>
    public static AuthorizationCacheOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var enabled = ParseBool(section[EnabledKey], EnabledKey, defaultValue: true);
        var ttl = ParseTtl(section[TtlKey], TtlKey, DefaultTtl);
        return new AuthorizationCacheOptions(enabled, ttl);
    }

    private static bool ParseBool(string? value, string key, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{key}' is not a valid boolean.");
        }

        return parsed;
    }

    private static TimeSpan ParseTtl(string? value, string key, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"Configuration value '{ConfigurationSection}:{key}' is not a valid TimeSpan.");
        }

        return parsed;
    }
}
