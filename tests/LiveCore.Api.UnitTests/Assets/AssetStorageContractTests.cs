using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Contract tests for the <see cref="IAssetStorage"/> storage adapter port (CORE-AST-002), exercised
/// through a conforming in-memory fake adapter (the deployment supplies the real, provider-specific
/// S3-compatible adapter — docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006). They assert the security
/// properties EVERY adapter must uphold: it only ever hands back a <see cref="SignedAssetUrl"/> (never a
/// public/static URL), the URL is short-lived (within <see cref="SignedAssetUrl.MaxLifetime"/>) and not
/// expired at issue, it is tagged with the requested operation, and it is signed for the GIVEN asset's own
/// storage coordinates — never an arbitrary object. Together this is the adapter-level expression of the
/// epic acceptance criterion: "Assets are private by default and accessed only through authorized signed
/// URLs" (threat T4 "Asset leak" in docs/07_SECURITY_THREAT_MODEL.md). Generic Asset vocabulary only
/// (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class AssetStorageContractTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);

    private static Asset CreateAsset(string bucket, string objectKey)
        => Asset.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "s3",
            bucket,
            objectKey,
            "image/png",
            _now);

    [Fact]
    public async Task CreateUploadUrlAsync_yields_a_short_lived_signed_upload_url_for_the_assets_object()
    {
        IAssetStorage storage = new FakeSignedUrlAssetStorage(_now);
        var asset = CreateAsset("livecore-private-assets", "org/ws/asset-upload.bin");

        var signed = await storage.CreateUploadUrlAsync(asset, CancellationToken.None);

        Assert.Equal(AssetStorageOperation.Upload, signed.Operation);
        Assert.True(signed.Url.IsAbsoluteUri);
        // Short-lived and still valid at issue (threat T4; docs/12_STORAGE_ASSETS.md "signed URLs are
        // short-lived").
        Assert.False(signed.IsExpired(_now));
        Assert.True(signed.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
        // Signed for THIS asset's own coordinates, not an arbitrary bucket/object (threats T5/T1).
        Assert.Contains(asset.Bucket, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_yields_a_short_lived_signed_download_url_for_the_assets_object()
    {
        IAssetStorage storage = new FakeSignedUrlAssetStorage(_now);
        var asset = CreateAsset("livecore-private-assets", "org/ws/asset-download.bin");

        var signed = await storage.CreateDownloadUrlAsync(asset, CancellationToken.None);

        Assert.Equal(AssetStorageOperation.Download, signed.Operation);
        Assert.False(signed.IsExpired(_now));
        Assert.True(signed.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteObjectAsync_completes_for_the_assets_object_and_rejects_a_null_asset()
    {
        // The server-side delete (CORE-AST-006) hands back no URL and only ever removes access, so it
        // cannot weaken the private-by-default posture. A conforming adapter completes the delete and still
        // guards against a null asset.
        IAssetStorage storage = new FakeSignedUrlAssetStorage(_now);
        var asset = CreateAsset("livecore-private-assets", "org/ws/asset-delete.bin");

        await storage.DeleteObjectAsync(asset, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.DeleteObjectAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteObjectAsync_completes_for_explicit_coordinates_and_rejects_blank()
    {
        // The coordinate-addressed delete (CORE-PRIV-003) removes a NON-asset object whose lifecycle Core
        // manages — a completed export's produced artifact — by its recorded bucket/key. Like the asset delete
        // it hands back no URL and only ever removes access, so it cannot weaken the private-by-default posture.
        // A conforming adapter completes the delete and still guards against blank coordinates.
        IAssetStorage storage = new FakeSignedUrlAssetStorage(_now);

        await storage.DeleteObjectAsync("livecore-private-assets", "org/ws/export-artifact.bin", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.DeleteObjectAsync(" ", "org/ws/export-artifact.bin", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.DeleteObjectAsync("livecore-private-assets", " ", CancellationToken.None));
    }

    [Fact]
    public async Task Each_asset_signs_its_own_object_so_one_assets_url_never_addresses_another()
    {
        IAssetStorage storage = new FakeSignedUrlAssetStorage(_now);
        var first = CreateAsset("livecore-private-assets", "org/ws/first.bin");
        var second = CreateAsset("livecore-private-assets", "org/ws/second.bin");

        var firstUrl = await storage.CreateDownloadUrlAsync(first, CancellationToken.None);
        var secondUrl = await storage.CreateDownloadUrlAsync(second, CancellationToken.None);

        Assert.Contains("org/ws/first.bin", firstUrl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("org/ws/second.bin", firstUrl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("org/ws/second.bin", secondUrl.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>
    /// A minimal, deterministic in-memory <see cref="IAssetStorage"/> that stands in for a real,
    /// deployment-supplied S3-compatible adapter. It mints a short-lived signed URL for the asset's own
    /// coordinates; it never returns a public or non-expiring URL because <see cref="SignedAssetUrl"/>
    /// makes that unrepresentable. It is NOT a production signer (no real provider signature) — it exists
    /// only to exercise the port contract.
    /// </summary>
    private sealed class FakeSignedUrlAssetStorage(DateTimeOffset now) : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(15);

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => Task.FromResult(Sign(asset, AssetStorageOperation.Upload));

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => Task.FromResult(Sign(asset, AssetStorageOperation.Download));

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            // Server-side delete (CORE-AST-006): hands back no URL, only ever removes access. Idempotent —
            // deleting an object that was never uploaded completes successfully.
            ArgumentNullException.ThrowIfNull(asset);
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        {
            // Coordinate-addressed server-side delete (CORE-PRIV-003): hands back no URL, only ever removes
            // access, idempotent. Guards blank coordinates so a broken key can never address an object.
            if (string.IsNullOrWhiteSpace(bucket))
            {
                throw new ArgumentException("Bucket must not be blank.", nameof(bucket));
            }

            if (string.IsNullOrWhiteSpace(objectKey))
            {
                throw new ArgumentException("Object key must not be blank.", nameof(objectKey));
            }

            return Task.CompletedTask;
        }

        private SignedAssetUrl Sign(Asset asset, AssetStorageOperation operation)
        {
            ArgumentNullException.ThrowIfNull(asset);

            var verb = operation == AssetStorageOperation.Upload ? "put" : "get";
            var url = new Uri(
                $"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op={verb}&signature=fake-signature");

            return SignedAssetUrl.Create(url, operation, now, _lifetime);
        }
    }
}
