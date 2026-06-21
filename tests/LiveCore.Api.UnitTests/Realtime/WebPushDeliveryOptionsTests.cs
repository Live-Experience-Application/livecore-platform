// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Realtime;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Realtime;

/// <summary>
/// Unit tests for <see cref="WebPushDeliveryOptions"/> (CORE-PUSH-002) — the deployment's closed-app push delivery
/// configuration. They assert the surface is OFF BY DEFAULT and INERT, that <see cref="WebPushDeliveryOptions.IsActive"/>
/// requires BOTH the opt-in AND the full VAPID signing material (so an OUTBOUND-HTTP fan-out can never run on a
/// partial configuration), and that the cadence/batch/TTL are read with safe defaults and rejected when malformed.
/// Generic transport vocabulary only (AGENTS.md).
/// </summary>
public sealed class WebPushDeliveryOptionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> FullyConfigured() => new()
    {
        ["WebPush:Delivery:Enabled"] = "true",
        ["WebPush:Vapid:PublicKey"] = "the-public-key",
        ["WebPush:Vapid:PrivateKey"] = "the-private-key",
        ["WebPush:Vapid:Subject"] = "mailto:ops@example.test",
    };

    [Fact]
    public void FromConfiguration_is_off_and_inert_by_default()
    {
        var options = WebPushDeliveryOptions.FromConfiguration(Configuration(new Dictionary<string, string?>()));

        Assert.False(options.Enabled);
        Assert.False(options.IsActive);
        Assert.Null(options.VapidPublicKey);
        Assert.Null(options.VapidPrivateKey);
        Assert.Null(options.VapidSubject);
        Assert.Equal(WebPushDeliveryOptions.DefaultSweepInterval, options.SweepInterval);
        Assert.Equal(WebPushDeliveryOptions.DefaultBatchSize, options.BatchSize);
        Assert.Equal(WebPushDeliveryOptions.DefaultTimeToLive, options.TimeToLive);
    }

    [Fact]
    public void IsActive_when_enabled_with_full_vapid_material()
    {
        var options = WebPushDeliveryOptions.FromConfiguration(Configuration(FullyConfigured()));

        Assert.True(options.IsActive);
        Assert.Equal("the-public-key", options.VapidPublicKey);
        Assert.Equal("the-private-key", options.VapidPrivateKey);
        Assert.Equal("mailto:ops@example.test", options.VapidSubject);
    }

    [Fact]
    public void IsActive_is_false_when_enabled_but_vapid_is_configured_but_not_for_sending()
    {
        // The public key alone (the CORE-PUSH-001 subscription surface) does NOT activate delivery: signing needs
        // the private key and subject too. A partial configuration stays inert (no undeliverable enqueue).
        var values = FullyConfigured();
        values.Remove("WebPush:Vapid:PrivateKey");
        var options = WebPushDeliveryOptions.FromConfiguration(Configuration(values));

        Assert.True(options.Enabled);
        Assert.False(options.IsActive);
    }

    [Fact]
    public void IsActive_is_false_when_vapid_is_configured_but_delivery_is_not_enabled()
    {
        // VAPID configured but the opt-in flag absent: delivery stays off (off by default).
        var values = FullyConfigured();
        values.Remove("WebPush:Delivery:Enabled");
        var options = WebPushDeliveryOptions.FromConfiguration(Configuration(values));

        Assert.False(options.Enabled);
        Assert.False(options.IsActive);
    }

    [Fact]
    public void FromConfiguration_reads_the_cadence_batch_and_ttl()
    {
        var values = FullyConfigured();
        values["WebPush:Delivery:SweepInterval"] = "00:00:30";
        values["WebPush:Delivery:BatchSize"] = "250";
        values["WebPush:Delivery:TimeToLive"] = "01:00:00";
        var options = WebPushDeliveryOptions.FromConfiguration(Configuration(values));

        Assert.Equal(TimeSpan.FromSeconds(30), options.SweepInterval);
        Assert.Equal(250, options.BatchSize);
        Assert.Equal(TimeSpan.FromHours(1), options.TimeToLive);
    }

    [Theory]
    [InlineData("WebPush:Delivery:Enabled", "maybe")]
    [InlineData("WebPush:Delivery:SweepInterval", "not-a-timespan")]
    [InlineData("WebPush:Delivery:BatchSize", "not-an-int")]
    [InlineData("WebPush:Delivery:TimeToLive", "nonsense")]
    public void FromConfiguration_rejects_a_malformed_value(string key, string value)
    {
        var values = FullyConfigured();
        values[key] = value;

        Assert.ThrowsAny<Exception>(() => WebPushDeliveryOptions.FromConfiguration(Configuration(values)));
    }
}
