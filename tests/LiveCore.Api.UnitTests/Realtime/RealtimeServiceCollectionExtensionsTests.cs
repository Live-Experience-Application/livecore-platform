// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="RealtimeServiceCollectionExtensions.AddLiveCoreRealtime"/> (CORE-OPS-007), the
/// CONDITIONAL selection of the realtime scale-out backplane from the deployment's <c>Realtime:Backplane:*</c>
/// configuration. They pin the security-critical invariants:
/// <list type="bullet">
///   <item>
///   With a backplane connection string configured, the SignalR <c>HubLifetimeManager</c> is the
///   Redis/Valkey-backed one, so a group send fans out to connections on every API instance (the epic
///   acceptance criterion).
///   </item>
///   <item>
///   Unconfigured, SignalR keeps its in-memory <see cref="DefaultHubLifetimeManager{THub}"/> — the documented
///   single-instance fallback.
///   </item>
///   <item>
///   In BOTH cases the <see cref="IRealtimeBackplane"/> is the same <see cref="InProcessRealtimeBackplane"/>
///   and SignalR core is registered, so enabling scale-out does not change the send path or the per-recipient
///   recipient computation — the backplane still only transports an already-authorized delivery and cannot
///   widen the audience (threat T3).
///   </item>
/// </list>
/// Descriptors are inspected without building the provider, so no Redis client is constructed and no
/// connection is attempted. Generic Realtime vocabulary only (AGENTS.md).
/// </summary>
public class RealtimeServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Type? RegisteredBackplaneType(IServiceCollection services)
        => services.Single(d => d.ServiceType == typeof(IRealtimeBackplane)).ImplementationType;

    // SignalR registers HubLifetimeManager<> as an OPEN generic; AddStackExchangeRedis appends the Redis
    // implementation after the default, so the last descriptor is the one that wins at resolution time.
    private static Type? ResolvedHubLifetimeManager(IServiceCollection services)
        => services.Last(d => d.ServiceType == typeof(HubLifetimeManager<>)).ImplementationType;

    [Fact]
    public void AddLiveCoreRealtime_keeps_the_in_process_default_when_unconfigured()
    {
        var services = new ServiceCollection();

        var configured = services.AddLiveCoreRealtime(new ConfigurationBuilder().Build());

        Assert.False(configured);
        // SignalR's in-memory single-instance lifetime manager stays — the documented single-instance fallback.
        Assert.Equal(typeof(DefaultHubLifetimeManager<>), ResolvedHubLifetimeManager(services));
        // The send path is the in-process backplane (unchanged), so recipient computation is unaffected.
        Assert.Equal(typeof(InProcessRealtimeBackplane), RegisteredBackplaneType(services));
    }

    [Fact]
    public void AddLiveCoreRealtime_keeps_the_in_process_default_for_a_blank_connection_string()
    {
        var services = new ServiceCollection();

        // A present-but-blank connection string is treated as unconfigured (no half-wired backplane).
        var configured = services.AddLiveCoreRealtime(Configuration(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = "   ",
        }));

        Assert.False(configured);
        Assert.Equal(typeof(DefaultHubLifetimeManager<>), ResolvedHubLifetimeManager(services));
        Assert.Equal(typeof(InProcessRealtimeBackplane), RegisteredBackplaneType(services));
    }

    [Fact]
    public void AddLiveCoreRealtime_selects_the_redis_backplane_when_configured()
    {
        var services = new ServiceCollection();

        var configured = services.AddLiveCoreRealtime(Configuration(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = "valkey:6379",
        }));

        Assert.True(configured);

        // The Redis/Valkey-backed lifetime manager is selected, so a group send reaches connections on every
        // API instance (the type is internal to the StackExchangeRedis package, so it is matched by name).
        var lifetimeManager = ResolvedHubLifetimeManager(services);
        Assert.NotNull(lifetimeManager);
        Assert.Equal("RedisHubLifetimeManager`1", lifetimeManager.Name);
        Assert.Contains("StackExchangeRedis", lifetimeManager.Namespace);
        Assert.NotEqual(typeof(DefaultHubLifetimeManager<>), lifetimeManager);
    }

    [Fact]
    public void AddLiveCoreRealtime_does_not_change_the_send_path_when_the_backplane_is_configured()
    {
        var services = new ServiceCollection();

        services.AddLiveCoreRealtime(Configuration(new Dictionary<string, string?>
        {
            ["Realtime:Backplane:ConnectionString"] = "valkey:6379",
            ["Realtime:Backplane:ChannelPrefix"] = "livecore",
        }));

        // Enabling scale-out swaps only the transport beneath IHubContext: the IRealtimeBackplane is still the
        // in-process implementation, and SignalR core is still registered, so the recipient computation and the
        // one-payload-to-one-group send path are unchanged (it cannot widen the audience — threat T3).
        Assert.Equal(typeof(InProcessRealtimeBackplane), RegisteredBackplaneType(services));
        Assert.Contains(services, d => d.ServiceType == typeof(HubLifetimeManager<>));
    }

    [Fact]
    public void AddLiveCoreRealtime_rejects_null_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddLiveCoreRealtime(null!));
        Assert.Throws<ArgumentNullException>(
            () => RealtimeServiceCollectionExtensions.AddLiveCoreRealtime(null!, new ConfigurationBuilder().Build()));
    }
}
