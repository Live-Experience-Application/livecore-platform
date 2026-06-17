// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.Runtime;
using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="S3AssetStorageOptions"/> (CORE-OPS-006) — the deployment's S3-compatible
/// connection settings the concrete adapter is wired from. They assert the settings are read from
/// configuration only (so no credential lives in source; threat T7), the optional values fall back to safe
/// defaults, a missing or PARTIAL configuration reports "unconfigured" so the registration stays fail-closed
/// (threat T4 "Asset leak"), and a configured URL lifetime is validated against the short-lived
/// <see cref="SignedAssetUrl.MaxLifetime"/> ceiling. Generic Asset vocabulary only (AGENTS.md).
/// </summary>
public class S3AssetStorageOptionsTests
{
    private static S3AssetStorageOptions FromValues(IDictionary<string, string?> values)
        => S3AssetStorageOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void FromConfiguration_is_unconfigured_with_safe_defaults_when_unset()
    {
        var options = S3AssetStorageOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.IsConfigured);
        Assert.Null(options.Endpoint);
        Assert.Null(options.AccessKeyId);
        Assert.Null(options.SecretAccessKey);
        Assert.Equal(S3AssetStorageOptions.DefaultRegion, options.Region);
        Assert.True(options.ForcePathStyle);
        Assert.Equal(S3AssetStorageOptions.DefaultUrlLifetime, options.UrlLifetime);
        // Outbound-call bounds (CORE-RES-005): short, safe defaults so a hung storage backend fails fast.
        Assert.Equal(S3AssetStorageOptions.DefaultRequestTimeout, options.RequestTimeout);
        Assert.Equal(S3AssetStorageOptions.DefaultMaxErrorRetry, options.MaxErrorRetry);
        Assert.Equal(S3AssetStorageOptions.DefaultRetryMode, options.RetryMode);
    }

    [Fact]
    public void Defaults_for_the_outbound_call_bounds_are_short_and_safe()
    {
        // The defaults must keep a real ceiling well below the AWS SDK's 100-second default, with a bounded,
        // non-negative retry count and a defined retry mode (CORE-RES-005).
        Assert.True(S3AssetStorageOptions.DefaultRequestTimeout > TimeSpan.Zero);
        Assert.True(S3AssetStorageOptions.DefaultRequestTimeout < TimeSpan.FromSeconds(100));
        Assert.True(S3AssetStorageOptions.DefaultMaxErrorRetry >= 0);
        Assert.True(Enum.IsDefined(S3AssetStorageOptions.DefaultRetryMode));
    }

    [Fact]
    public void FromConfiguration_reads_and_trims_the_configured_connection_settings()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "  https://storage.example.com  ",
            ["Assets:Storage:AccessKeyId"] = "  access-key  ",
            ["Assets:Storage:SecretAccessKey"] = "secret-key",
            ["Assets:Storage:Region"] = "  eu-west-1  ",
            ["Assets:Storage:ForcePathStyle"] = "false",
            ["Assets:Storage:UrlLifetime"] = "00:30:00",
        });

        Assert.True(options.IsConfigured);
        Assert.Equal("https://storage.example.com", options.Endpoint);
        Assert.Equal("access-key", options.AccessKeyId);
        Assert.Equal("secret-key", options.SecretAccessKey);
        Assert.Equal("eu-west-1", options.Region);
        Assert.False(options.ForcePathStyle);
        Assert.Equal(TimeSpan.FromMinutes(30), options.UrlLifetime);
    }

    [Theory]
    [InlineData(null, "key", "secret")]
    [InlineData("https://storage.example.com", null, "secret")]
    [InlineData("https://storage.example.com", "key", null)]
    [InlineData("   ", "key", "secret")]
    public void FromConfiguration_is_unconfigured_when_any_required_value_is_missing(
        string? endpoint,
        string? accessKeyId,
        string? secretAccessKey)
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = endpoint,
            ["Assets:Storage:AccessKeyId"] = accessKeyId,
            ["Assets:Storage:SecretAccessKey"] = secretAccessKey,
        });

        // A partial configuration is treated as unconfigured, so the registration keeps the fail-closed
        // UnconfiguredAssetStorage and asset access denies cleanly rather than half-wiring a signer (threat T4).
        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    [InlineData("01:00:01")]
    [InlineData("1.00:00:00")]
    public void FromConfiguration_rejects_a_lifetime_outside_the_short_lived_ceiling(string lifetime)
    {
        Assert.Throws<ArgumentException>(() => FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:UrlLifetime"] = lifetime,
        }));
    }

    [Fact]
    public void FromConfiguration_accepts_the_maximum_lifetime()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:UrlLifetime"] = "01:00:00",
        });

        Assert.Equal(SignedAssetUrl.MaxLifetime, options.UrlLifetime);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_outbound_call_bounds()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:RequestTimeout"] = "00:00:05",
            ["Assets:Storage:MaxErrorRetry"] = "1",
            ["Assets:Storage:RetryMode"] = "adaptive",
        });

        Assert.Equal(TimeSpan.FromSeconds(5), options.RequestTimeout);
        Assert.Equal(1, options.MaxErrorRetry);
        // The retry mode is parsed case-insensitively into the SDK enum.
        Assert.Equal(RequestRetryMode.Adaptive, options.RetryMode);
    }

    [Fact]
    public void FromConfiguration_allows_zero_max_error_retry_to_disable_retries()
    {
        var options = FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:MaxErrorRetry"] = "0",
        });

        Assert.Equal(0, options.MaxErrorRetry);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public void FromConfiguration_rejects_a_non_positive_request_timeout(string requestTimeout)
    {
        // A non-positive Timeout would let the SDK fall back to its 100-second default, defeating fail-fast.
        Assert.Throws<ArgumentException>(() => FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:RequestTimeout"] = requestTimeout,
        }));
    }

    [Fact]
    public void FromConfiguration_rejects_a_negative_max_error_retry()
    {
        Assert.Throws<ArgumentException>(() => FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:MaxErrorRetry"] = "-1",
        }));
    }

    [Fact]
    public void FromConfiguration_rejects_an_unrecognised_retry_mode()
    {
        Assert.Throws<ArgumentException>(() => FromValues(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "key",
            ["Assets:Storage:SecretAccessKey"] = "secret",
            ["Assets:Storage:RetryMode"] = "not-a-retry-mode",
        }));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
        => Assert.Throws<ArgumentNullException>(() => S3AssetStorageOptions.FromConfiguration(null!));
}
