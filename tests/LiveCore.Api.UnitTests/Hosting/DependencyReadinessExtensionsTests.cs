// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.Runtime;
using Amazon.S3;
using LiveCore.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiveCore.Api.UnitTests.Hosting;

/// <summary>
/// Unit tests for the object-storage half of <see cref="DependencyReadinessExtensions"/> (CORE-OBS-009). They
/// pin the "reads only run when storage is configured" acceptance criterion of CORE-OBS-014: the live
/// <see cref="ObjectStorageReadinessProbe"/> is wired ONLY when a complete <c>Assets:Storage</c> configuration is
/// present, so an unconfigured host — where the fail-closed unconfigured storage is in place and there is no
/// backend to reach — never runs the probe (and so the fail-closed read never fires against absent storage).
/// Generic, product-neutral vocabulary only (AGENTS.md).
/// </summary>
public class DependencyReadinessExtensionsTests
{
    private static readonly Dictionary<string, string?> _configuredStorage = new()
    {
        ["Assets:Storage:Endpoint"] = "https://storage.example.test",
        ["Assets:Storage:AccessKeyId"] = "test-access-key",
        ["Assets:Storage:SecretAccessKey"] = "test-secret-key",
    };

    [Fact]
    public void Registers_the_object_storage_probe_when_storage_is_configured()
    {
        using var provider = BuildProvider(_configuredStorage);

        Assert.Contains(
            provider.GetServices<IDependencyReadinessProbe>(),
            probe => probe is ObjectStorageReadinessProbe);
    }

    [Fact]
    public void Does_not_register_the_object_storage_probe_when_storage_is_unconfigured()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        // Reads only run when storage is configured (CORE-OBS-014): with no storage config the live probe is
        // never wired, so the fail-closed unconfigured storage is never read against.
        Assert.DoesNotContain(
            provider.GetServices<IDependencyReadinessProbe>(),
            probe => probe is ObjectStorageReadinessProbe);
    }

    [Fact]
    public void Does_not_register_the_object_storage_probe_on_a_partial_configuration()
    {
        // A partial configuration (endpoint but no credentials) is treated as unconfigured by S3AssetStorageOptions,
        // so the host keeps the fail-closed unconfigured storage and the probe must not be wired.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Assets:Storage:Endpoint"] = "https://storage.example.test",
        });

        Assert.DoesNotContain(
            provider.GetServices<IDependencyReadinessProbe>(),
            probe => probe is ObjectStorageReadinessProbe);
    }

    private static ServiceProvider BuildProvider(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        // The storage probe reuses the SAME IAmazonS3 the storage adapter registers (CORE-OPS-006); provide a
        // stand-in (static fake credentials, a custom ServiceURL — never reaches a backend) so the registered
        // factory can resolve when storage IS configured.
        services.AddSingleton<IAmazonS3>(new AmazonS3Client(
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            new AmazonS3Config { ServiceURL = "https://storage.example.test", ForcePathStyle = true }));

        services.AddDeepDependencyReadinessChecks(configuration);

        return services.BuildServiceProvider();
    }
}
