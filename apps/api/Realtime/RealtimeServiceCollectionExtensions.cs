// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using StackExchange.Redis;

namespace LiveCore.Api.Realtime;

/// <summary>
/// Dependency-injection wiring for the Realtime module's SignalR services and the realtime scale-out backplane
/// (CORE-OPS-007). It always adds SignalR and the in-process <see cref="IRealtimeBackplane"/>, and CONDITIONALLY
/// — on the deployment's <c>Realtime:Backplane:*</c> configuration (<see cref="RealtimeBackplaneOptions"/>) —
/// makes the SignalR backplane Redis/Valkey-backed:
/// <list type="bullet">
///   <item>
///   CONFIGURED (a connection string is present): <c>AddStackExchangeRedis</c> replaces the in-memory SignalR
///   <c>HubLifetimeManager</c> with the Redis-backed one, so every group send through
///   <see cref="Microsoft.AspNetCore.SignalR.IHubContext{SessionHub}"/> is published over Redis pub/sub and
///   reaches the connections held by EVERY API instance — realtime events reach clients across multiple
///   instances (the epic acceptance criterion; docs/11_REALTIME_SYNC.md "Scale-out").
///   </item>
///   <item>
///   UNCONFIGURED: SignalR keeps its default in-memory <c>HubLifetimeManager</c>, which delivers correctly for
///   a SINGLE API instance only — the documented single-instance constraint. This is fail-SAFE, not a security
///   downgrade: delivery is still per-recipient authorized (see below), so a single-instance deployment is
///   correct and a multi-instance one simply must configure a backplane to stop dropping events to other
///   replicas.
///   </item>
/// </list>
/// CRUCIALLY, enabling the Redis backplane DOES NOT CHANGE the recipient computation or the send path. The
/// <see cref="IRealtimeBackplane"/> stays the in-process <see cref="InProcessRealtimeBackplane"/> in both cases;
/// the <c>SessionEventPublisher</c> still hands it one ALREADY-AUTHORIZED, already-projected per-recipient
/// delivery (one recipient-safe payload to exactly ONE server-computed group, produced by the per-recipient
/// <c>SessionEventRecipientResolver</c>). The Redis backplane only transports that group send across instances;
/// it has no event, no visibility subject and no way to enumerate or broaden the audience, so it cannot widen
/// what the resolver already authorized. The recipient resolver therefore stays the single send path and
/// "Realtime delivery never leaks hidden events" (threat T3) holds with or without scale-out, by construction
/// (docs/05_MODULE_CONTRACTS.md: the Realtime module "may not send unfiltered events").
///
/// No backplane connection string is read anywhere but configuration (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md); the concrete Redis client lives behind this seam
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md).
/// </summary>
public static class RealtimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Realtime SignalR services and the <see cref="IRealtimeBackplane"/>, selecting the
    /// Redis/Valkey-backed SignalR backplane when a connection string is configured under
    /// <c>Realtime:Backplane:*</c>, otherwise leaving the in-process single-instance default. Returns whether
    /// the Redis backplane was wired, so a caller can branch on it (the API host ignores it; the result
    /// documents the choice for tests).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (<c>Realtime:Backplane:*</c>).</param>
    /// <returns><see langword="true"/> when the Redis/Valkey backplane was wired.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static bool AddLiveCoreRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = RealtimeBackplaneOptions.FromConfiguration(configuration);

        // SignalR is part of the ASP.NET Core shared framework (no new package for the in-process default).
        var signalR = services.AddSignalR();

        if (options.IsConfigured)
        {
            // Scale-out: make the SignalR HubLifetimeManager Redis/Valkey-backed so a group send fans out to
            // connections on every API instance. This changes only the TRANSPORT beneath IHubContext; the
            // per-recipient recipient computation and the one-payload-to-one-group send path are unchanged, so
            // the audience cannot be widened (threat T3).
            signalR.AddStackExchangeRedis(options.ConnectionString!, redis =>
            {
                if (!string.IsNullOrWhiteSpace(options.ChannelPrefix))
                {
                    // Namespace this deployment's pub/sub channels so one Redis/Valkey instance can be shared
                    // (e.g. backplane + cache) without channel collisions.
                    redis.Configuration.ChannelPrefix = RedisChannel.Literal(options.ChannelPrefix);
                }
            });
        }

        // The IRealtimeBackplane seam is unchanged either way: it forwards each already-authorized delivery to
        // its server-computed SignalR group over IHubContext. With the Redis backplane wired above, that same
        // forward now reaches connections on other instances; without it, only this instance's connections.
        services.AddSingleton<IRealtimeBackplane, InProcessRealtimeBackplane>();

        // Connection re-authorization / eviction (CORE-RTC-002). The registry is the singleton record of the
        // live connections THIS instance holds: the SessionHub writes an admitted connection on connect and
        // clears it on disconnect, and the IRealtimeConnectionEvictor it implements aborts exactly the
        // connections a participant removal or a member role change affects (so their still-open sockets stop
        // receiving events they are no longer authorized to see, not only on reconnect). It only ever removes
        // a connection, so it can never widen an audience (threat T3). Registered UNCONDITIONALLY (no database
        // dependency) so the evictor seam is available to the persistence-conditional Sessions command that
        // raises it; the same singleton instance backs both registrations.
        services.AddSingleton<RealtimeConnectionRegistry>();
        services.AddSingleton<IRealtimeConnectionEvictor>(sp => sp.GetRequiredService<RealtimeConnectionRegistry>());

        return options.IsConfigured;
    }
}
