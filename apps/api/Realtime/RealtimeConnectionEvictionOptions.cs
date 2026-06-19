// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// Configuration for cross-instance realtime-connection eviction (CORE-RES-008, the "Multi-Instance Runtime
/// Correctness" epic). The per-instance <see cref="RealtimeConnectionRegistry"/> aborts only the sockets it holds on
/// a demotion/removal; this feature broadcasts the eviction to the other replicas over the deployment's
/// already-configured Valkey/Redis backplane (the SAME <c>Realtime:Backplane:*</c> connection the realtime fan-out
/// uses, docs/02_ARCHITECTURE.md names a Valkey/Redis-compatible backplane for realtime scale-out AND cache).
///
/// <para>
/// The connection settings are REUSED from <see cref="RealtimeBackplaneOptions"/> — there is no second backplane to
/// configure, and no connection string lives in source (threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The one
/// dedicated knob is an opt-out toggle (<c>Realtime:CrossInstanceEviction</c>, ON by default), exactly like the
/// authorization-cache invalidation toggle (CORE-RES-007), so an operator can fall back to the previous
/// single-instance posture without disabling the realtime backplane:
/// </para>
///
/// <list type="bullet">
///   <item><b><see cref="Enabled"/></b> — whether cross-instance eviction is on. ON by default. Turning it off
///   (<c>Realtime:CrossInstanceEviction=false</c>) keeps eviction correct on the instance that handles the change but
///   reverts to NOT propagating it — a socket on another replica then lingers until that client reconnects, the
///   pre-CORE-RES-008 behaviour, never a widened audience.</item>
///   <item><b><see cref="IsActive"/></b> — whether the feature actually wires: it needs BOTH the toggle on AND a
///   backplane connection string present (a single-instance deployment has none, so it stays on the
///   <see cref="NullRealtimeConnectionEvictionBackplane"/> no-op).</item>
/// </list>
///
/// <para>
/// No secret lives here (a flag, a reused connection string and a derived channel name) and the values are
/// product-neutral (AGENTS.md).
/// </para>
/// </summary>
internal sealed class RealtimeConnectionEvictionOptions
{
    /// <summary>Configuration section the toggle is read from (<c>Realtime</c>, the parent of <c>Realtime:Backplane</c>).</summary>
    public const string ConfigurationSection = "Realtime";

    /// <summary>Configuration key under <see cref="ConfigurationSection"/> toggling cross-instance eviction on/off.</summary>
    public const string CrossInstanceEnabledKey = "CrossInstanceEviction";

    /// <summary>
    /// The base pub/sub channel the eviction descriptors are published on. Namespaced by the realtime backplane's
    /// <see cref="RealtimeBackplaneOptions.ChannelPrefix"/> (when set) exactly like the SignalR channels, so two
    /// deployments sharing one Valkey/Redis server never cross evictions, and distinct from the SignalR fan-out
    /// channels and the authorization-cache invalidation channel so the three never collide.
    /// </summary>
    public const string ChannelSuffix = "livecore:realtime:eviction";

    private RealtimeConnectionEvictionOptions(bool enabled, string? connectionString, string channel)
    {
        Enabled = enabled;
        ConnectionString = connectionString;
        Channel = channel;
    }

    /// <summary>Whether cross-instance eviction is enabled by configuration (the opt-out toggle; ON by default).</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The Valkey/Redis connection string the eviction channel uses, reused from <c>Realtime:Backplane:ConnectionString</c>.
    /// Null when no backplane is configured. Read from configuration only; never hardcoded (threat T7).
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>The fully-qualified pub/sub channel name (the deployment's channel prefix plus <see cref="ChannelSuffix"/>).</summary>
    public string Channel { get; }

    /// <summary>
    /// Whether the feature actually wires the Redis backplane: the toggle is on AND a backplane connection string is
    /// configured. With either absent the host keeps the <see cref="NullRealtimeConnectionEvictionBackplane"/> no-op
    /// (single-instance behaviour, the registry evicts its own held sockets only).
    /// </summary>
    public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Reads the cross-instance eviction options: the opt-out toggle from <c>Realtime:CrossInstanceEviction</c>
    /// (default ON), and the connection string and channel prefix from the realtime backplane configuration
    /// (<c>Realtime:Backplane:*</c>). A toggle value that is PRESENT but not a valid boolean is rejected at startup
    /// rather than silently falling back, exactly like the authorization-cache invalidation options (CORE-RES-007).
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="InvalidOperationException">The toggle value is present but not a valid boolean.</exception>
    public static RealtimeConnectionEvictionOptions FromConfiguration(IConfiguration configuration)
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

        return new RealtimeConnectionEvictionOptions(enabled, backplane.ConnectionString, channel);
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
