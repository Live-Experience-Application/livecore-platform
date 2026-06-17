using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveCore.Api.Assets;

/// <summary>
/// Dependency-injection wiring for the asset storage adapter seam (CORE-OPS-006). It registers the single
/// <see cref="IAssetStorage"/> the Assets module's flows consume, choosing the implementation CONDITIONALLY
/// on the deployment's <c>Assets:Storage:*</c> configuration (<see cref="S3AssetStorageOptions"/>):
/// <list type="bullet">
///   <item>
///   CONFIGURED (endpoint + credentials present): the concrete <see cref="S3CompatibleAssetStorage"/> over
///   an SDK <see cref="IAmazonS3"/> client, so the API mints real pre-signed upload/download URLs against the
///   S3-compatible backend (the epic acceptance criterion).
///   </item>
///   <item>
///   UNCONFIGURED (or partially configured): the fail-closed <see cref="UnconfiguredAssetStorage"/>, so every
///   asset operation throws <see cref="AssetStorageNotConfiguredException"/> and the consuming endpoints
///   surface 503 — assets stay private by default even when storage is not configured (threat T4 "Asset
///   leak"). This mirrors how the host runs without a database connection string or an OIDC authority and
///   denies cleanly rather than degrading security.
///   </item>
/// </list>
/// Keeping the choice in one PUBLIC extension lets both hosts wire the SAME adapter without duplicating the
/// selection logic: the API host calls it directly, and the worker's
/// <see cref="AssetCleanupServiceCollectionExtensions.AddAssetCleanup"/> calls it so the background cleanup
/// job deletes real objects when storage is configured. No storage credential is read anywhere but
/// configuration (threat T7 in docs/07_SECURITY_THREAT_MODEL.md); the concrete client lives behind this seam
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006).
/// </summary>
public static class AssetStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAssetStorage"/> as the concrete S3-compatible adapter when the deployment's
    /// storage endpoint and credentials are configured under <c>Assets:Storage:*</c>, otherwise as the
    /// fail-closed <see cref="UnconfiguredAssetStorage"/>. Returns whether a concrete adapter was registered,
    /// so a caller can branch on it (the API host ignores it; the result documents the choice for tests).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration (<c>Assets:Storage:*</c>).</param>
    /// <returns><see langword="true"/> when the concrete S3-compatible adapter was registered.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A configured URL lifetime is non-positive or exceeds the ceiling.</exception>
    public static bool AddAssetStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = S3AssetStorageOptions.FromConfiguration(configuration);
        if (!options.IsConfigured)
        {
            // Fail-closed default: assets stay private by default and every operation denies (503) until a
            // real, deployment-supplied S3-compatible backend is configured (threat T4).
            services.AddSingleton<IAssetStorage, UnconfiguredAssetStorage>();
            return false;
        }

        // The signing clock the adapter stamps the URL's issue/expiry with. TryAdd so this never conflicts
        // with the TimeProvider the persistence/cleanup wiring also registers (same singleton instance).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<IAmazonS3>(_ => CreateClient(options));
        services.AddSingleton<IAssetStorage, S3CompatibleAssetStorage>();
        return true;
    }

    /// <summary>
    /// Builds the SDK S3 client from the configured connection settings. <c>ServiceURL</c> points the client
    /// at the deployment's own S3-compatible endpoint (rather than an AWS region endpoint), and path-style
    /// addressing is the default for self-hosted backends. Static credentials are taken from configuration
    /// only (threat T7); constructing the client performs no network call.
    ///
    /// Outbound calls are BOUNDED (CORE-RES-005): the per-request <c>Timeout</c>, a bounded <c>MaxErrorRetry</c>
    /// and a bounded <c>RetryMode</c> come from the configured (short, safe) defaults so a hung storage backend
    /// fails fast — a real-network <c>DeleteObjectAsync</c> aborts at the configured timeout rather than blocking
    /// a worker thread up to the AWS SDK's 100-second default — and a failing call cannot amplify unbounded.
    /// </summary>
    private static IAmazonS3 CreateClient(S3AssetStorageOptions options)
    {
        // Modern S3-compatible backends (RustFS/MinIO and current AWS S3) require AWS Signature Version 4 for
        // pre-signed URLs; legacy SigV2 is rejected by them, and the SDK otherwise falls back to SigV2 for a
        // custom ServiceURL. This is the documented AWS knob for S3 pre-signing; it is process-global, which is
        // correct here because S3 is the only AWS service this process uses, and it is set deterministically at
        // wiring time (before any request).
        AWSConfigsS3.UseSignatureVersion4 = true;

        var config = new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,

            // Bound the one storage operation that makes a real network round-trip (DeleteObjectAsync) so a hung
            // dependency fails fast instead of holding a thread up to the AWS SDK's 100-second default; the
            // bounded retry count/mode keep a failing call from amplifying unbounded (CORE-RES-005). Minting a
            // pre-signed URL is local (no round-trip), so these only ever affect the server-side delete.
            Timeout = options.RequestTimeout,
            MaxErrorRetry = options.MaxErrorRetry,
            RetryMode = options.RetryMode,
        };

        var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
        return new AmazonS3Client(credentials, config);
    }
}
