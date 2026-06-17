// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// The deployment's PRIVATE object-storage naming for newly created assets (CORE-AST-003, the upload
/// intent flow of the "Asset Storage and Authorization" epic). When the upload-intent flow registers a
/// new <see cref="Asset"/> it must record WHERE the object will live — its <see cref="Asset.StorageProvider"/>
/// and <see cref="Asset.Bucket"/> (docs/12_STORAGE_ASSETS.md metadata; the object key itself is minted
/// per-asset by <see cref="AssetUploadIntentService"/>). The provider and bucket are deployment naming, not
/// per-request input: every asset of a deployment lives in the SAME private bucket, and the bucket name
/// must match the one the deployment's signed-URL adapter signs against, so the two are read from
/// configuration here, never chosen by a client.
///
/// This is the metadata-naming half of the storage seam, distinct from the
/// <see cref="IAssetStorage"/> adapter that mints the signed URL: docs/13_SELF_HOSTING_REQUIREMENTS.md
/// lists "object storage endpoint and credentials" as runtime configuration, and the bucket name is the
/// part of that naming Core must record on the row (the endpoint and credentials stay inside the
/// deployment-supplied adapter — Core carries no storage SDK dependency and no credentials in source,
/// threat T7 in docs/07_SECURITY_THREAT_MODEL.md). The configured value names a PRIVATE bucket
/// (docs/12_STORAGE_ASSETS.md "buckets private by default"); nothing here makes an asset public — the
/// object is reached only through an authorized, short-lived signed URL (threat T4 "Asset leak"). The
/// value is product-neutral (a generic provider/bucket name, never vertical vocabulary; AGENTS.md).
///
/// When no configuration is supplied the host still runs (mirroring how it starts without a database
/// connection string or an OIDC authority) using the safe, private-by-default <see cref="DefaultProvider"/>
/// and <see cref="DefaultBucket"/>; a real deployment overrides them under
/// <see cref="ConfigurationSection"/>. A configured value that is not a valid storage token (the same
/// invariants the <see cref="Asset"/> aggregate enforces) is rejected at startup rather than silently
/// recording a broken coordinate.
/// </summary>
internal sealed class AssetStorageLocation
{
    /// <summary>Configuration section the provider and bucket are read from (<c>Assets:Storage</c>).</summary>
    public const string ConfigurationSection = "Assets:Storage";

    /// <summary>
    /// Default storage provider when none is configured: a generic S3-compatible backend identifier
    /// (docs/12_STORAGE_ASSETS.md; docs/02_ARCHITECTURE.md "S3-compatible storage abstraction").
    /// </summary>
    public const string DefaultProvider = "s3";

    /// <summary>
    /// Default private bucket when none is configured. Private by default — assets are never publicly
    /// reachable (threat T4); a deployment overrides this with its own private bucket name.
    /// </summary>
    public const string DefaultBucket = "livecore-assets";

    /// <summary>
    /// Creates a storage location naming. Both values are validated against the same token invariants the
    /// <see cref="Asset"/> aggregate enforces for its storage coordinates (non-blank, bounded, no
    /// whitespace or control characters), so a broken configured value can never reach a persisted asset.
    /// </summary>
    /// <param name="provider">The S3-compatible storage provider identifier (for example <c>s3</c>).</param>
    /// <param name="bucket">The private bucket new assets are stored in.</param>
    /// <exception cref="ArgumentException">The provider or bucket violates the storage token invariants.</exception>
    public AssetStorageLocation(string provider, string bucket)
    {
        var normalizedProvider = provider?.Trim() ?? string.Empty;
        var normalizedBucket = bucket?.Trim() ?? string.Empty;

        if (!Asset.IsValidStorageProvider(normalizedProvider))
        {
            throw new ArgumentException("Storage provider violates the storage provider invariants.", nameof(provider));
        }

        if (!Asset.IsValidBucket(normalizedBucket))
        {
            throw new ArgumentException("Bucket violates the bucket invariants.", nameof(bucket));
        }

        Provider = normalizedProvider;
        Bucket = normalizedBucket;
    }

    /// <summary>The S3-compatible storage provider new assets are recorded against.</summary>
    public string Provider { get; }

    /// <summary>The private bucket new assets are stored in.</summary>
    public string Bucket { get; }

    /// <summary>
    /// Reads the storage location from configuration under <see cref="ConfigurationSection"/>
    /// (<c>Assets:Storage:Provider</c> / <c>Assets:Storage:Bucket</c>), falling back to the private,
    /// safe-by-default <see cref="DefaultProvider"/> / <see cref="DefaultBucket"/> when a value is absent
    /// or blank. No storage credentials are read here (only the naming); the endpoint and credentials live
    /// with the deployment-supplied adapter (docs/13_SELF_HOSTING_REQUIREMENTS.md; threat T7).
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    /// <exception cref="ArgumentException">A configured provider or bucket violates the storage invariants.</exception>
    public static AssetStorageLocation FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var provider = section["Provider"];
        var bucket = section["Bucket"];

        return new AssetStorageLocation(
            string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider,
            string.IsNullOrWhiteSpace(bucket) ? DefaultBucket : bucket);
    }
}
