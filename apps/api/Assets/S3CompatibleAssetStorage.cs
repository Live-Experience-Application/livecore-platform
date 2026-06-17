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
/// BINDS THE DECLARED TYPE AND SIZE INTO AN UPLOAD URL (CORE-AST-008). An UPLOAD URL additionally signs the
/// asset's DECLARED content type and content length (<see cref="BindUploadConstraints"/>), so the storage
/// backend itself rejects an upload whose actual type or byte count violates what was declared at
/// upload-intent — closing the declare-small / upload-large bypass of the workspace storage quota
/// (CORE-MON-006) and the CORE-AST-007 ceiling/allowlist, which only ever see the DECLARED metadata. A
/// download URL is unchanged (it serves an already-stored object). The binding adds only signed request
/// conditions; it never widens access (threats T4/T7).
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

        // CORE-AST-008 — BIND the declared content type and content length into an UPLOAD URL, so the storage
        // backend ITSELF rejects an upload whose bytes or type violate what was declared at upload-intent.
        // Before this the presigned PUT bound only bucket/key/verb/expiry, so a client could declare 1 byte of
        // an allowed MIME (passing the CORE-AST-007 allowlist/ceiling and the CORE-MON-006 quota, which only
        // ever see the DECLARED metadata) and then PUT a multi-gigabyte object of any type — bypassing both the
        // workspace storage quota and the per-object ceiling. The declared values live on the asset row itself
        // (its content type is always set; its size is the bytes reserved against the quota), so binding them
        // needs no extra input. A DOWNLOAD URL is unchanged — it serves an already-stored object, so there is
        // nothing to constrain. The privacy model is untouched: the URL is still a short-lived, signed,
        // single-object secret and the binding adds only signed REQUEST conditions, never a storage coordinate
        // to any caller-visible surface (threats T4/T7).
        if (operation == AssetStorageOperation.Upload)
        {
            BindUploadConstraints(request, asset);
        }

        // Pure local SigV4 query signing — no network round-trip.
        var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);

        // SignedAssetUrl re-validates the invariants (absolute, short-lived), so a misbehaving SDK result can
        // never become a public or long-lived URL.
        return SignedAssetUrl.Create(new Uri(url, UriKind.Absolute), operation, issuedAt, _urlLifetime);
    }

    /// <summary>
    /// Binds the asset's DECLARED content type and content length into the pre-signed upload request as SigV4
    /// SIGNED conditions (CORE-AST-008), so the storage backend itself enforces them on the actual PUT:
    /// <list type="bullet">
    ///   <item>
    ///   <see cref="GetPreSignedUrlRequest.ContentType"/> adds <c>content-type</c> to the request's signed
    ///   headers, so a PUT whose <c>Content-Type</c> differs from the type declared (and validated against the
    ///   CORE-AST-007 allowlist) at upload-intent fails the signature check at storage.
    ///   </item>
    ///   <item>
    ///   The declared object size (the bytes reserved against the CORE-MON-006 quota) is signed as the
    ///   <c>Content-Length</c> header, so a PUT that sends a different number of bytes — in particular MORE than
    ///   the declared/reserved size — fails the signature check at storage. The size is always present for an
    ///   upload intent (the command rejects a non-positive size before minting a URL); the guard keeps the
    ///   adapter safe if it is ever asked to sign an upload for a sizeless asset.
    ///   </item>
    /// </list>
    /// The asset's own declared metadata is the single source of truth — the same values the quota and the
    /// CORE-AST-007 acceptance policy already saw — so a client can no longer declare a small/allowed object and
    /// then upload a large one of any type. Nothing here weakens the privacy model: the binding adds signed
    /// REQUEST conditions only, never a public affordance and never a storage coordinate on a caller surface
    /// (threats T4/T7).
    /// </summary>
    private static void BindUploadConstraints(GetPreSignedUrlRequest request, Asset asset)
    {
        request.ContentType = asset.ContentType;

        if (asset.SizeBytes is { } declaredSize)
        {
            request.Headers.ContentLength = declaredSize;
        }
    }
}
