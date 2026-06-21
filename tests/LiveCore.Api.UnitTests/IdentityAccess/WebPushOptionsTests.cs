// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.IdentityAccess;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.IdentityAccess;

/// <summary>
/// Unit tests for <see cref="WebPushOptions"/> (CORE-PUSH-001) — the deployment's VAPID public-key configuration
/// behind the closed-app push surface. They assert the surface is INERT (unconfigured) by default so the host
/// runs without any push configuration, a configured key is read and trimmed, a blank value stays inert, and an
/// over-length key is rejected at startup. Generic transport vocabulary only (AGENTS.md).
/// </summary>
public class WebPushOptionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_is_inert_when_no_key_is_configured()
    {
        var options = WebPushOptions.FromConfiguration(Configuration(new Dictionary<string, string?>()));

        Assert.False(options.IsConfigured);
        Assert.Null(options.PublicKey);
    }

    [Fact]
    public void FromConfiguration_reads_and_trims_the_configured_key()
    {
        var options = WebPushOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["WebPush:Vapid:PublicKey"] = "  BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA-key  ",
        }));

        Assert.True(options.IsConfigured);
        Assert.Equal("BNcRdreALRFXTkOOUHK1EtK2wtaz5Ry4YfYCA-key", options.PublicKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromConfiguration_treats_a_blank_key_as_inert(string value)
    {
        var options = WebPushOptions.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            ["WebPush:Vapid:PublicKey"] = value,
        }));

        Assert.False(options.IsConfigured);
        Assert.Null(options.PublicKey);
    }

    [Fact]
    public void FromConfiguration_rejects_an_over_length_key()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["WebPush:Vapid:PublicKey"] = new string('a', WebPushOptions.MaxPublicKeyLength + 1),
        });

        Assert.Throws<ArgumentException>(() => WebPushOptions.FromConfiguration(configuration));
    }
}
