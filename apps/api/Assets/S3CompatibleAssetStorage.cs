// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using Amazon.S3;
using Amazon.S3.Model;

namespace LiveCore.Api.Assets;

/// <summary>
/// The concrete, S3-compatible <see cref="IAssetStorage"/> adapter (CORE-OPS-006) — the production signer
/// that replaces the fail-closed <see cref="UnconfiguredAssetStorage"/> once a deployment configures object
/// storage. It implements the CORE-AST-002 storage port over the AWS SDK for .NET S3 client
/// (<see cref="IAmazonS3"/>), which speaks the S3 protocol against any S3-compatible backend — RustFS
/// self-hosted or any S3-compatible provider hosted (docs/02_ARCHITECTURE.md "S3-compatible storage
/// abstraction"; docs/12_STORAGE_ASSETS.md; ADR 0006). The connection endpoint and credentials come from
/// <see cref="S3AssetStorageOptions"/> (configuration only; threat T7); they are held inside the
/// SDK-supplied <see cref="IAmazonS3"/> client, never on an asset row.
///
/// HONORS THE SECURITY CONTRACT structurally. The only access this adapter ever yields is a
/// <see cref="SignedAssetUrl"/>, whose type makes a public, relative, non-expiring or long-lived URL
/// UNREPRESENTABLE (absolute + lifetime within <see cref="SignedAssetUrl.MaxLifetime"/>). It signs ONLY the
/// given, already-resolved asset's OWN coordinates (<see cref="Asset.Bucket"/> + <see cref="Asset.ObjectKey"/>),
/// never an arbitrary caller-supplied bucket/key, so a minted URL can only ever address an object inside the
/// caller's tenant and workspace (threats T5/T1; the epic acceptance criterion; threat T4 "Asset leak").
/// Authorization is upstream (the upload-intent / signed-download endpoints authorize server-side BEFORE
/// asking this adapter to mint a URL); this adapter is a dumb, secure signer and decides nothing about
/// access.
///
/// The pre-signed URL is produced LOCALLY by the SDK from the credentials and the request (SigV4 query
/// signing) — minting a URL performs no network round-trip — while <see cref="DeleteObjectAsync(Asset, System.Threading.CancellationToken)"/> performs
/// a real, server-side delete with the deployment's own credentials (no URL is handed to any client, so it
/// can only ever REMOVE access; threat T4).
/// </summary>
internal sealed class S3CompatibleAssetStorage : IAssetStorage
{
    private readonly IAmazonS3 _s3;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _urlLifetime;

    public S3CompatibleAssetStorage(IAmazonS3 s3, S3AssetStorageOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _s3 = s3;
        _timeProvider = timeProvider;
        _urlLifetime = options.UrlLifetime;
    }

    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        => SignAsync(asset, AssetStorageOperation.Upload, HttpVerb.PUT, cancellationToken);

    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        => SignAsync(asset, AssetStorageOperation.Download, HttpVerb.GET, cancellationToken);

    /// <inheritdoc />
    public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // Delete the asset's OWN coordinates through the coordinate-addressed delete below; the asset is the
        // already-resolved, tenant- and workspace-scoped metadata row, so this addresses only its own object.
        return DeleteObjectAsync(asset.Bucket, asset.ObjectKey, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException("Bucket must not be blank.", nameof(bucket));
        }

        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("Object key must not be blank.", nameof(objectKey));
        }

        // Server-side delete with the deployment's own credentials — no signed URL is produced (threat T4).
        // S3 DeleteObject is idempotent: deleting a key that does not exist (a pending intent whose client never
        // uploaded, or an export with no produced blob) returns success, so the cleanup/retention jobs can
        // reclaim a record whether or not its object was ever written (CORE-AST-006, CORE-PRIV-003).
        var request = new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = objectKey,
        };

        await _s3.DeleteObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SignedAssetUrl> SignAsync(
        Asset asset,
        AssetStorageOperation operation,
        HttpVerb verb,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();

        var issuedAt = _timeProvider.GetUtcNow();

        // Sign ONLY this asset's own coordinates (threats T5/T1). Expires is the absolute instant the URL
        // stops working; the SDK derives the short SigV4 X-Amz-Expires window from it. The lifetime is bounded
        // by S3AssetStorageOptions (<= SignedAssetUrl.MaxLifetime), so the URL is always short-lived.
        var request = new GetPreSignedUrlRequest
        {
            BucketName = asset.Bucket,
            Key = asset.ObjectKey,
            Verb = verb,
            Expires = issuedAt.Add(_urlLifetime).UtcDateTime,
        };

        // Pure local SigV4 query signing — no network round-trip.
        var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);

        // SignedAssetUrl re-validates the invariants (absolute, short-lived), so a misbehaving SDK result can
        // never become a public or long-lived URL.
        return SignedAssetUrl.Create(new Uri(url, UriKind.Absolute), operation, issuedAt, _urlLifetime);
    }
}
