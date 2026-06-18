// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;

namespace LiveCore.Api.Persistence;

/// <summary>
/// Configuration for the cross-instance authorization-cache invalidation (CORE-RES-007, the "Multi-Instance Runtime
/// Correctness" epic). The per-process <see cref="AuthorizationLookupCache"/> evicts only its own instance on a
/// revocation; this feature broadcasts the eviction to the other replicas over the deployment's already-configured
/// Valkey/Redis backplane (the SAME <c>Realtime:Backplane:*</c> connection the realtime scale-out uses,
/// docs/02_ARCHITECTURE.md names a Valkey/Redis-compatible backplane for realtime scale-out AND cache).
///
/// <para>
/// The connection settings are REUSED from <see cref="RealtimeBackplaneOptions"/> — there is no second backplane to
/// configure, and no connection string lives in source (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The one
/// dedicated knob is an opt-out toggle (<c>AuthorizationCache:CrossInstanceInvalidation</c>, ON by default) so an
/// operator can fall back to the TTL-only behaviour without disabling the realtime backplane:
/// </para>
///
/// <list type="bullet">
///   <item><b><see cref="Enabled"/></b> — whether cross-instance invalidation is on. ON by default. Turning it off
///   (<c>AuthorizationCache:CrossInstanceInvalidation=false</c>) keeps the cache correct but reverts the peer-replica
///   window to the <see cref="AuthorizationCacheOptions.Ttl"/> backstop only — a change to the eventual-consistency
///   window, never to an authorization decision (the cache is still positive-only and locally invalidated).</item>
///   <item><b><see cref="IsActive"/></b> — whether the feature actually wires: it needs BOTH the toggle on AND a
///   backplane connection string present (a single-instance deployment has none, so it stays on the
///   <see cref="NullAuthorizationCacheInvalidationBackplane"/> no-op and the TTL backstop).</item>
/// </list>
///
/// <para>
/// No secret lives here (a flag, a reused connection string and a derived channel name) and the values are
/// product-neutral (AGENTS.md).
/// </para>
/// </summary>
internal sealed class AuthorizationCacheInvalidationOptions
{
    /// <summary>Configuration section the toggle is read from (<c>AuthorizationCache</c>, shared with <see cref="AuthorizationCacheOptions"/>).</summary>
    public const string ConfigurationSection = AuthorizationCacheOptions.ConfigurationSection;

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> toggling cross-instance invalidation on/off.</summary>
    public const string CrossInstanceEnabledKey = "CrossInstanceInvalidation";

    /// <summary>
    /// The base pub/sub channel the invalidation messages are published on. Namespaced by the realtime backplane's
    /// <see cref="RealtimeBackplaneOptions.ChannelPrefix"/> (when set) exactly like the SignalR channels, so two
    /// deployments sharing one Valkey/Redis server never cross invalidations, and distinct from the SignalR channels
    /// so realtime fan-out and cache invalidation never collide.
    /// </summary>
    public const string ChannelSuffix = "livecore:authz-cache:invalidation";

    private AuthorizationCacheInvalidationOptions(bool enabled, string? connectionString, string channel)
    {
        Enabled = enabled;
        ConnectionString = connectionString;
        Channel = channel;
    }

    /// <summary>Whether cross-instance invalidation is enabled by configuration (the opt-out toggle; ON by default).</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The Valkey/Redis connection string the invalidation channel uses, reused from <c>Realtime:Backplane:ConnectionString</c>.
    /// Null when no backplane is configured. Read from configuration only; never hardcoded (threat T7).
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>The fully-qualified pub/sub channel name (the deployment's channel prefix plus <see cref="ChannelSuffix"/>).</summary>
    public string Channel { get; }

    /// <summary>
    /// Whether the feature actually wires the Redis backplane: the toggle is on AND a backplane connection string is
    /// configured. With either absent the host keeps the <see cref="NullAuthorizationCacheInvalidationBackplane"/>
    /// no-op (the TTL backstop is the documented window).
    /// </summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Reads the cross-instance invalidation options: the opt-out toggle from <c>AuthorizationCache:CrossInstanceInvalidation</c>
    /// (default ON), and the connection string and channel prefix from the realtime backplane configuration
    /// (<c>Realtime:Backplane:*</c>). A toggle value that is PRESENT but not a valid boolean is rejected at startup
    /// rather than silently falling back, exactly like <see cref="AuthorizationCacheOptions.FromConfiguration"/>.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">The toggle value is present but not a valid boolean.</exception>
    public static AuthorizationCacheInvalidationOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ParseBool(
            configuration.GetSection(ConfigurationSection)[CrossInstanceEnabledKey],
            CrossInstanceEnabledKey,
            defaultValue: true);

        // Reuse the realtime backplane connection (the same Valkey/Redis server); there is no second backplane.
        var backplane = RealtimeBackplaneOptions.FromConfiguration(configuration);
        var channel = string.IsNullOrWhiteSpace(backplane.ChannelPrefix)
            ? ChannelSuffix
            : string.Concat(backplane.ChannelPrefix, ChannelSuffix);

        return new AuthorizationCacheInvalidationOptions(enabled, backplane.ConnectionString, channel);
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
}
