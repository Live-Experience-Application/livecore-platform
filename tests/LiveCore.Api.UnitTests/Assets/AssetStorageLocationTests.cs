// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetStorageLocation"/> (CORE-AST-003) — the deployment's PRIVATE
/// provider/bucket naming new assets are recorded against. They assert the configuration is read with
/// safe, private-by-default fallbacks (so the host runs without storage configuration, mirroring the
/// database/OIDC defaults), explicit configured values are honored, and an invalid configured value is
/// rejected against the same storage-token invariants the <see cref="Asset"/> aggregate enforces (a broken
/// coordinate can never reach a persisted asset). Generic Asset vocabulary only (AGENTS.md).
/// </summary>
public class AssetStorageLocationTests
{
    [Fact]
    public void Constructor_trims_and_keeps_valid_provider_and_bucket()
    {
        var location = new AssetStorageLocation("  s3  ", "  livecore-private-assets  ");

        Assert.Equal("s3", location.Provider);
        Assert.Equal("livecore-private-assets", location.Bucket);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    public void Constructor_rejects_an_invalid_provider(string provider)
    {
        Assert.Throws<ArgumentException>(() => new AssetStorageLocation(provider, AssetStorageLocation.DefaultBucket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    public void Constructor_rejects_an_invalid_bucket(string bucket)
    {
        Assert.Throws<ArgumentException>(() => new AssetStorageLocation(AssetStorageLocation.DefaultProvider, bucket));
    }

    [Fact]
    public void FromConfiguration_falls_back_to_the_private_defaults_when_unset()
    {
        var configuration = new ConfigurationBuilder().Build();

        var location = AssetStorageLocation.FromConfiguration(configuration);

        Assert.Equal(AssetStorageLocation.DefaultProvider, location.Provider);
        Assert.Equal(AssetStorageLocation.DefaultBucket, location.Bucket);
    }

    [Fact]
    public void FromConfiguration_reads_the_configured_provider_and_bucket()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Storage:Provider"] = "rustfs",
                ["Assets:Storage:Bucket"] = "deployment-private-bucket",
            })
            .Build();

        var location = AssetStorageLocation.FromConfiguration(configuration);

        Assert.Equal("rustfs", location.Provider);
        Assert.Equal("deployment-private-bucket", location.Bucket);
    }

    [Fact]
    public void FromConfiguration_falls_back_per_field_when_only_one_value_is_set()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Storage:Bucket"] = "only-the-bucket-is-set",
            })
            .Build();

        var location = AssetStorageLocation.FromConfiguration(configuration);

        Assert.Equal(AssetStorageLocation.DefaultProvider, location.Provider);
        Assert.Equal("only-the-bucket-is-set", location.Bucket);
    }

    [Fact]
    public void FromConfiguration_rejects_a_configured_value_that_violates_the_storage_invariants()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Storage:Bucket"] = "invalid bucket with spaces",
            })
            .Build();

        Assert.Throws<ArgumentException>(() => AssetStorageLocation.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_rejects_a_null_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => AssetStorageLocation.FromConfiguration(null!));
    }
}
