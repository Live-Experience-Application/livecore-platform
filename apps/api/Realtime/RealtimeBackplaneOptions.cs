// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Realtime;

/// <summary>
/// The deployment's realtime scale-out backplane CONNECTION settings (CORE-OPS-007, the "Production
/// Operations Readiness" epic) — the Redis/Valkey-compatible server the SignalR backplane publishes group
/// sends over so a realtime delivery reaches the connections held by EVERY API instance, not just the one
/// that computed it (docs/11_REALTIME_SYNC.md "Scale-out"; docs/02_ARCHITECTURE.md names a Valkey/Redis-compatible
/// backplane for realtime scale-out). docs/13_SELF_HOSTING_REQUIREMENTS.md lists the realtime backplane as
/// optional runtime configuration; it is read here from configuration ONLY (the <c>Realtime:Backplane:*</c>
/// keys, e.g. the environment variable <c>Realtime__Backplane__ConnectionString</c>), never hardcoded — no
/// backplane connection string lives in this repository (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
///
/// OPTIONAL, FAIL-SAFE WHEN UNCONFIGURED. <see cref="IsConfigured"/> is true only when a connection string is
/// present; with nothing configured the host keeps the in-process SignalR backplane
/// (<see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/>), which delivers correctly for a
/// SINGLE API instance — the documented single-instance constraint (a multi-replica deployment without a
/// configured backplane would silently drop events to connections on other replicas, so configuring this is
/// required for HA/scale). Unlike the asset-storage and OIDC seams, leaving this unconfigured is not a
/// security downgrade: realtime delivery is still per-recipient authorized either way (the backplane only ever
/// transports an already-computed, already-authorized per-recipient delivery; it never widens the audience).
///
/// The values are the generic Redis/Valkey connection vocabulary (a connection string and an optional channel
/// prefix); nothing here carries vertical domain language (AGENTS.md).
/// </summary>
internal sealed class RealtimeBackplaneOptions
{
    /// <summary>Configuration section the backplane connection settings are read from (<c>Realtime:Backplane</c>).</summary>
    public const string ConfigurationSection = "Realtime:Backplane";

    private RealtimeBackplaneOptions(string? connectionString, string? channelPrefix)
    {
        ConnectionString = connectionString;
        ChannelPrefix = channelPrefix;
    }

    /// <summary>
    /// The Redis/Valkey-compatible connection string the SignalR backplane connects to (StackExchange.Redis
    /// format, e.g. <c>valkey:6379</c> or <c>host:6379,password=…,ssl=true</c>). Read from configuration only;
    /// never hardcoded (threat T7).
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>
    /// An optional channel-name prefix that namespaces this deployment's SignalR pub/sub channels on the Redis
    /// server. It lets one Valkey/Redis instance be shared between the realtime backplane and other uses (a
    /// cache, or another app) without their channels colliding (docs/02_ARCHITECTURE.md pairs the backplane
    /// with the cache on the same Redis-compatible server). Optional; unset means the framework default.
    /// </summary>
    public string? ChannelPrefix { get; }

    /// <summary>
    /// Whether a Redis/Valkey backplane should be wired: a connection string is present. With it absent the
    /// host keeps the in-process backplane (correct for a single API instance only — the documented
    /// single-instance constraint).
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Reads the backplane connection settings from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Realtime:Backplane:ConnectionString</c> and the optional <c>Realtime:Backplane:ChannelPrefix</c>),
    /// trimming surrounding whitespace. A missing connection string yields an unconfigured value, so the host
    /// keeps the in-process backplane (the documented single-instance fallback).
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static RealtimeBackplaneOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var connectionString = section["ConnectionString"];
        var channelPrefix = section["ChannelPrefix"];

        return new RealtimeBackplaneOptions(
            string.IsNullOrWhiteSpace(connectionString) ? null : connectionString.Trim(),
            string.IsNullOrWhiteSpace(channelPrefix) ? null : channelPrefix.Trim());
    }
}
