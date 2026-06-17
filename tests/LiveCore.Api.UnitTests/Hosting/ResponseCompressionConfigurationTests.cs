// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for <see cref="ResponseCompressionConfiguration.ResponseCompressionSettings"/> (CORE-PERF-006) —
/// the response-compression settings read from <c>ResponseCompression:*</c>. They pin the ON-by-default posture,
/// the HTTPS-compression default, the configuration toggles, the null guard and the JSON-only MIME allow-list.
/// The end-to-end behavior (a large list is compressed, the hub transport is not, content is unchanged) is
/// covered over real HTTP by the integration suite.
/// </summary>
public class ResponseCompressionConfigurationTests
{
    [Fact]
    public void Read_defaults_to_on_with_https_compression_enabled()
    {
        var settings = ResponseCompressionConfiguration.ResponseCompressionSettings.Read(
            new ConfigurationBuilder().Build());

        // The bandwidth optimization is on out of the box, including over HTTPS (the mobile/list/feed/replay
        // surface this story targets is served over TLS).
        Assert.True(settings.Enabled);
        Assert.True(settings.EnableForHttps);
    }

    [Fact]
    public void Read_honors_the_toggles()
    {
        var settings = ResponseCompressionConfiguration.ResponseCompressionSettings.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ResponseCompression:Enabled"] = "false",
                    ["ResponseCompression:EnableForHttps"] = "false",
                })
                .Build());

        Assert.False(settings.Enabled);
        Assert.False(settings.EnableForHttps);
    }

    [Fact]
    public void Read_can_disable_https_compression_while_leaving_the_feature_on()
    {
        var settings = ResponseCompressionConfiguration.ResponseCompressionSettings.Read(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ResponseCompression:EnableForHttps"] = "false",
                })
                .Build());

        Assert.True(settings.Enabled);
        Assert.False(settings.EnableForHttps);
    }

    [Fact]
    public void Read_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(
            () => ResponseCompressionConfiguration.ResponseCompressionSettings.Read(null!));
    }

    [Fact]
    public void Compressible_mime_types_are_json_only()
    {
        var types = ResponseCompressionConfiguration.CompressibleJsonMimeTypes;

        // Exactly the two JSON content types the API serves — the list/feed/replay payload and the Problem
        // Details error shape. Nothing else (no text/plain metrics, no HTML, no binary) is in the allow-list, so
        // only JSON is ever compressed and already-compressed/binary content passes through untouched.
        Assert.Contains("application/json", types);
        Assert.Contains("application/problem+json", types);
        Assert.Equal(2, types.Count);
        Assert.DoesNotContain("text/plain", types);
        Assert.DoesNotContain("text/html", types);
    }
}
