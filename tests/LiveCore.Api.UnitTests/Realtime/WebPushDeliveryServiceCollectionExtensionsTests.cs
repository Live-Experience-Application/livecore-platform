// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="WebPushDeliveryServiceCollectionExtensions.AddWebPushDelivery"/> (CORE-PUSH-002), the
/// off-by-default GATING of the worker's closed-app push dispatch sweep. They pin: the sweep is registered ONLY when
/// a database is configured AND delivery is active (enabled with the full VAPID signing material); a missing
/// connection string, the off-by-default flag, or partial VAPID material all leave it inert (returns false, registers
/// no dispatch service). Descriptors are inspected without building the provider, so no database connection is
/// attempted. Generic transport vocabulary only (AGENTS.md).
/// </summary>
public sealed class WebPushDeliveryServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ActiveConfig() => new()
    {
        ["ConnectionStrings:Database"] = "Host=localhost;Database=livecore",
        ["WebPush:Delivery:Enabled"] = "true",
        ["WebPush:Vapid:PublicKey"] = "the-public-key",
        ["WebPush:Vapid:PrivateKey"] = "the-private-key",
        ["WebPush:Vapid:Subject"] = "mailto:ops@example.test",
    };

    [Fact]
    public void AddWebPushDelivery_registers_the_dispatch_sweep_when_active()
    {
        var services = new ServiceCollection();

        var configured = services.AddWebPushDelivery(Configuration(ActiveConfig()));

        Assert.True(configured);
        Assert.Contains(services, d => d.ServiceType == typeof(PushNotificationDispatchService));
        Assert.Contains(services, d => d.ServiceType == typeof(IWebPushSender));
        Assert.Contains(services, d => d.ServiceType == typeof(IPushNotificationDeliveryRepository));
        Assert.Contains(services, d => d.ServiceType == typeof(WebPushDeliveryOptions));
    }

    [Fact]
    public void AddWebPushDelivery_is_inert_when_no_database_is_configured()
    {
        var services = new ServiceCollection();
        var values = ActiveConfig();
        values.Remove("ConnectionStrings:Database");

        var configured = services.AddWebPushDelivery(Configuration(values));

        Assert.False(configured);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PushNotificationDispatchService));
    }

    [Fact]
    public void AddWebPushDelivery_is_inert_when_delivery_is_off_by_default()
    {
        var services = new ServiceCollection();
        var values = ActiveConfig();
        values.Remove("WebPush:Delivery:Enabled");

        var configured = services.AddWebPushDelivery(Configuration(values));

        Assert.False(configured);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PushNotificationDispatchService));
    }

    [Fact]
    public void AddWebPushDelivery_is_inert_when_vapid_is_not_fully_configured()
    {
        // Enabled with only the public key (the subscription surface) is not enough to sign a push: inert.
        var services = new ServiceCollection();
        var values = ActiveConfig();
        values.Remove("WebPush:Vapid:PrivateKey");

        var configured = services.AddWebPushDelivery(Configuration(values));

        Assert.False(configured);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PushNotificationDispatchService));
    }

    [Fact]
    public void AddWebPushDelivery_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddWebPushDelivery(Configuration(ActiveConfig())));
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddWebPushDelivery(null!));
    }
}
