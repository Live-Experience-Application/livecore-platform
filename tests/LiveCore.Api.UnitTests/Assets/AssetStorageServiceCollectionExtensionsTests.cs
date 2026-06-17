using Amazon.Runtime;
using Amazon.S3;
using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetStorageServiceCollectionExtensions.AddAssetStorage"/> (CORE-OPS-006), the
/// CONDITIONAL registration that selects the storage adapter from the deployment's <c>Assets:Storage:*</c>
/// configuration. They assert the security-critical selection: with an endpoint and credentials configured
/// the concrete <see cref="S3CompatibleAssetStorage"/> (plus its SDK client) is wired so the API mints real
/// pre-signed URLs, and with nothing (or a partial) configuration the fail-closed
/// <see cref="UnconfiguredAssetStorage"/> stays — so unconfigured storage denies cleanly (the consuming
/// endpoints return 503) rather than being served some insecure way (the epic acceptance criterion; threat
/// T4 "Asset leak"). Descriptors are inspected without building the provider, so no SDK client is constructed.
/// Generic Asset vocabulary only (AGENTS.md).
/// </summary>
public class AssetStorageServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Type? RegisteredStorageType(IServiceCollection services)
        => services.Single(d => d.ServiceType == typeof(IAssetStorage)).ImplementationType;

    [Fact]
    public void AddAssetStorage_registers_the_fail_closed_default_when_unconfigured()
    {
        var services = new ServiceCollection();

        var configured = services.AddAssetStorage(new ConfigurationBuilder().Build());

        Assert.False(configured);
        Assert.Equal(typeof(UnconfiguredAssetStorage), RegisteredStorageType(services));
        // Fail-closed: no SDK client is wired when storage is unconfigured.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAmazonS3));
    }

    [Fact]
    public void AddAssetStorage_is_fail_closed_for_a_partial_configuration()
    {
        var services = new ServiceCollection();

        // Endpoint set but credentials missing: treated as unconfigured (no half-wired signer).
        var configured = services.AddAssetStorage(Configuration(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
        }));

        Assert.False(configured);
        Assert.Equal(typeof(UnconfiguredAssetStorage), RegisteredStorageType(services));
    }

    [Fact]
    public void AddAssetStorage_registers_the_concrete_adapter_when_configured()
    {
        var services = new ServiceCollection();

        var configured = services.AddAssetStorage(Configuration(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "access-key",
            ["Assets:Storage:SecretAccessKey"] = "secret-key",
        }));

        Assert.True(configured);
        Assert.Equal(typeof(S3CompatibleAssetStorage), RegisteredStorageType(services));
        // The SDK client, the options and the signing clock are all wired for the concrete adapter.
        Assert.Contains(services, d => d.ServiceType == typeof(IAmazonS3));
        Assert.Contains(services, d => d.ServiceType == typeof(S3AssetStorageOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void AddAssetStorage_applies_the_configured_outbound_call_bounds_to_the_s3_client()
    {
        // The wired SDK S3 client must carry the configured per-request timeout and bounded retry count/mode
        // (CORE-RES-005), so a hung storage delete fails fast rather than blocking up to the AWS SDK's
        // 100-second default. Constructing the client performs no network call.
        var services = new ServiceCollection();
        services.AddAssetStorage(Configuration(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "access-key",
            ["Assets:Storage:SecretAccessKey"] = "secret-key",
            ["Assets:Storage:RequestTimeout"] = "00:00:07",
            ["Assets:Storage:MaxErrorRetry"] = "1",
            ["Assets:Storage:RetryMode"] = "Standard",
        }));

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IAmazonS3>().Config;

        Assert.Equal(TimeSpan.FromSeconds(7), config.Timeout);
        Assert.Equal(1, config.MaxErrorRetry);
        Assert.Equal(RequestRetryMode.Standard, config.RetryMode);
    }

    [Fact]
    public void AddAssetStorage_applies_the_safe_default_outbound_call_bounds_when_unset()
    {
        var services = new ServiceCollection();
        services.AddAssetStorage(Configuration(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.com",
            ["Assets:Storage:AccessKeyId"] = "access-key",
            ["Assets:Storage:SecretAccessKey"] = "secret-key",
        }));

        using var provider = services.BuildServiceProvider();
        var config = provider.GetRequiredService<IAmazonS3>().Config;

        // A real ceiling well under the AWS SDK 100-second default, applied even with no storage tuning configured.
        Assert.Equal(S3AssetStorageOptions.DefaultRequestTimeout, config.Timeout);
        Assert.Equal(S3AssetStorageOptions.DefaultMaxErrorRetry, config.MaxErrorRetry);
        Assert.Equal(S3AssetStorageOptions.DefaultRetryMode, config.RetryMode);
    }

    [Fact]
    public void AddAssetStorage_rejects_null_arguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddAssetStorage(null!));
        Assert.Throws<ArgumentNullException>(
            () => AssetStorageServiceCollectionExtensions.AddAssetStorage(null!, new ConfigurationBuilder().Build()));
    }
}
