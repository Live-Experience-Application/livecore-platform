// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// The fail-closed default <see cref="IAssetStorage"/> (CORE-AST-002): used when no concrete,
/// S3-compatible object-storage adapter has been configured for the deployment. Every operation throws
/// <see cref="AssetStorageNotConfiguredException"/>, so no signed URL is ever produced without a real
/// storage backend behind it.
///
/// This is the storage analogue of the host running without a database connection string or without an
/// OIDC authority: the process still starts and the rest of the API works, but the asset feature DENIES
/// cleanly instead of degrading security. Crucially, "no storage configured" can never become "serve the
/// object some insecure way": assets stay private by default and are only ever reachable through a signed
/// URL minted by a real adapter (the epic acceptance criterion; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). The concrete provider adapter (and its SDK/credentials) is supplied
/// by the deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006), exactly as a Valkey/Redis backplane
/// replaces the in-process realtime default (CORE-RT-006); registering it overrides this default.
/// </summary>
internal sealed class UnconfiguredAssetStorage : IAssetStorage
{
    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
    {
        // Fail fast on a missing asset (programming error) before the fail-closed deny, mirroring the
        // ArgumentNullException.ThrowIfNull guard used throughout the codebase.
        ArgumentNullException.ThrowIfNull(asset);
        throw new AssetStorageNotConfiguredException(AssetStorageOperation.Upload);
    }

    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        throw new AssetStorageNotConfiguredException(AssetStorageOperation.Download);
    }

    /// <inheritdoc />
    public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
    {
        // Fail closed for deletion too (CORE-AST-006): with no configured adapter there is no object to
        // delete, and the cleanup job must NOT remove a metadata row whose object it could not delete (that
        // would orphan an object once storage IS configured). Throwing here makes the cleanup job leave the
        // record untouched rather than silently pretending success.
        ArgumentNullException.ThrowIfNull(asset);
        throw new AssetStorageNotConfiguredException(AssetStorageOperation.Delete);
    }

    /// <inheritdoc />
    public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        // Fail closed for the coordinate-addressed delete too (CORE-PRIV-003): with no configured adapter there
        // is no object to delete, and the retention sweep must NOT remove an export row whose artifact object it
        // could not delete (that would orphan the object once storage IS configured). Throwing here makes the
        // sweep leave the record untouched rather than silently pretending success.
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException("Bucket must not be blank.", nameof(bucket));
        }

        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("Object key must not be blank.", nameof(objectKey));
        }

        throw new AssetStorageNotConfiguredException(AssetStorageOperation.Delete);
    }
}
