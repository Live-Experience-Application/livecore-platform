// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;

namespace LiveCore.Api.Observability;

/// <summary>
/// Wraps the already-registered <see cref="IAssetStorage"/> with the failure-counting
/// <see cref="MeteredAssetStorage"/> decorator (CORE-OBS-001). A host calls this AFTER
/// <see cref="AssetStorageServiceCollectionExtensions.AddAssetStorage"/> (or the worker's
/// <c>AddAssetCleanup</c>, which calls it) so the metering wraps WHICHEVER adapter the conditional selection
/// chose — the concrete S3-compatible adapter or the fail-closed default — without changing that
/// selection. Keeping the decoration here, rather than inside <c>AddAssetStorage</c>, leaves the
/// security-critical selection logic and its tests untouched; this only adds an observability shim.
/// </summary>
public static class AssetStorageMetricsServiceCollectionExtensions
{
    /// <summary>
    /// Decorates the registered <see cref="IAssetStorage"/> singleton with <see cref="MeteredAssetStorage"/>,
    /// preserving the adapter's lifetime and behavior and adding only upload/download failure counting. The
    /// shared <see cref="LiveCoreMetrics"/> must be registered (<see cref="MetricsServiceCollectionExtensions.AddLiveCoreMetrics"/>).
    /// </summary>
    /// <param name="services">The service collection, after asset storage has been registered.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="IAssetStorage"/> is registered, or it was not registered by implementation type (the
    /// only shape <see cref="AssetStorageServiceCollectionExtensions.AddAssetStorage"/> produces).
    /// </exception>
    public static IServiceCollection AddLiveCoreAssetStorageMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(IAssetStorage))
            ?? throw new InvalidOperationException(
                "AddLiveCoreAssetStorageMetrics must be called after an IAssetStorage adapter is registered.");

        var implementationType = existing.ImplementationType
            ?? throw new InvalidOperationException(
                "The registered IAssetStorage is not registered by implementation type; cannot decorate it.");

        services.AddLiveCoreMetrics();

        // Re-register the concrete adapter under its own type (preserving the singleton lifetime), then
        // replace the IAssetStorage registration with the decorator resolving that concrete adapter. The
        // decorator is transparent: same behavior, plus failure counting.
        services.Remove(existing);
        services.AddSingleton(implementationType);
        services.AddSingleton<IAssetStorage>(provider => new MeteredAssetStorage(
            (IAssetStorage)provider.GetRequiredService(implementationType),
            provider.GetRequiredService<LiveCoreMetrics>()));

        return services;
    }
}
