using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using LiveCore.Api.Assets;
using Microsoft.Extensions.Configuration;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Contract tests for the concrete S3-compatible storage adapter <see cref="S3CompatibleAssetStorage"/>
/// (CORE-OPS-006), the production signer that replaces the fail-closed <see cref="UnconfiguredAssetStorage"/>
/// once a deployment configures object storage. They exercise the adapter over a REAL AWS SDK S3 client (the
/// SDK signs a pre-signed URL LOCALLY — no network round-trip — from static test credentials and a custom
/// <c>ServiceURL</c>), so the tests run offline and still prove the adapter produces a genuine pre-signed
/// URL. The delete path uses a recording client because a real delete would hit the network.
///
/// They assert the security properties the storage port requires of every adapter (the epic acceptance
/// criterion "honoring the SignedAssetUrl contract"; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>OP-CORRECT: an upload URL is tagged <see cref="AssetStorageOperation.Upload"/> (a PUT) and a
///   download URL <see cref="AssetStorageOperation.Download"/> (a GET).</item>
///   <item>ASSET-SCOPED: the URL is signed for the GIVEN asset's own bucket + object key, so one asset's URL
///   never addresses another's object (threats T5/T1).</item>
///   <item>SHORT-LIVED: the URL is absolute, not expired at issue, and expires within the configured
///   lifetime, never beyond the one-hour <see cref="SignedAssetUrl.MaxLifetime"/> ceiling.</item>
///   <item>The server-side delete addresses the asset's own coordinates and rejects a null asset.</item>
/// </list>
/// Generic Asset vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class S3CompatibleAssetStorageTests
{
    private const string _endpoint = "https://storage.example.com";
    private const string _bucket = "livecore-private-assets";
    private static readonly DateTimeOffset _now = new(2026, 6, 13, 9, 0, 0, TimeSpan.Zero);

    static S3CompatibleAssetStorageTests()
    {
        // Mirror the production wiring: the adapter forces AWS Signature Version 4 for S3 pre-signing (the
        // format modern S3-compatible backends require). The test builds the SDK client directly, so the
        // global is set here too so the signed URL is a genuine SigV4 pre-signed URL.
        AWSConfigsS3.UseSignatureVersion4 = true;
    }

    private static readonly S3AssetStorageOptions _options = S3AssetStorageOptions.FromConfiguration(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assets:Storage:Endpoint"] = _endpoint,
                ["Assets:Storage:AccessKeyId"] = "test-access-key",
                ["Assets:Storage:SecretAccessKey"] = "test-secret-key",
                ["Assets:Storage:Region"] = "us-east-1",
                ["Assets:Storage:UrlLifetime"] = "00:15:00",
            })
            .Build());

    private static Asset CreateAsset(string objectKey)
        => Asset.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "s3",
            _bucket,
            objectKey,
            "image/png",
            _now);

    private static S3CompatibleAssetStorage CreateStorage(IAmazonS3 client)
        => new(client, _options, new FixedTimeProvider(_now));

    [Fact]
    public async Task CreateUploadUrlAsync_mints_a_short_lived_signed_upload_url_for_the_assets_object()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/asset-upload.bin");

        var signed = await storage.CreateUploadUrlAsync(asset, CancellationToken.None);

        // Op-correct, absolute, asset-scoped.
        Assert.Equal(AssetStorageOperation.Upload, signed.Operation);
        Assert.True(signed.Url.IsAbsoluteUri);
        Assert.Contains(asset.Bucket, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        // A genuine SigV4 pre-signed URL (not a static/public link), pointed at the configured endpoint.
        Assert.StartsWith(_endpoint, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature", signed.Url.AbsoluteUri, StringComparison.Ordinal);
        // Short-lived: valid now and expiring exactly at the configured lifetime, within the one-hour ceiling.
        Assert.False(signed.IsExpired(_now));
        Assert.Equal(_now + _options.UrlLifetime, signed.ExpiresAt);
        Assert.True(signed.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_mints_a_short_lived_signed_download_url_for_the_assets_object()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/asset-download.bin");

        var signed = await storage.CreateDownloadUrlAsync(asset, CancellationToken.None);

        Assert.Equal(AssetStorageOperation.Download, signed.Operation);
        Assert.True(signed.Url.IsAbsoluteUri);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature", signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(signed.IsExpired(_now));
        Assert.Equal(_now + _options.UrlLifetime, signed.ExpiresAt);
        Assert.True(signed.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
    }

    [Fact]
    public async Task Each_asset_signs_its_own_object_so_one_assets_url_never_addresses_another()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var first = CreateAsset("org/ws/first.bin");
        var second = CreateAsset("org/ws/second.bin");

        var firstUrl = await storage.CreateDownloadUrlAsync(first, CancellationToken.None);
        var secondUrl = await storage.CreateDownloadUrlAsync(second, CancellationToken.None);

        Assert.Contains("org/ws/first.bin", firstUrl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("org/ws/second.bin", firstUrl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("org/ws/second.bin", secondUrl.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("org/ws/first.bin", secondUrl.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteObjectAsync_deletes_the_assets_own_object()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/asset-delete.bin");

        await storage.DeleteObjectAsync(asset, CancellationToken.None);

        // The delete addresses the asset's own coordinates only — never an arbitrary bucket/key (threats T5/T1).
        var request = Assert.Single(client.Deletes);
        Assert.Equal(asset.Bucket, request.BucketName);
        Assert.Equal(asset.ObjectKey, request.Key);
    }

    [Fact]
    public async Task DeleteObjectAsync_rejects_a_null_asset()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.DeleteObjectAsync(null!, CancellationToken.None));
        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task DeleteObjectAsync_by_coordinates_addresses_exactly_those_coordinates()
    {
        // The coordinate-addressed delete (CORE-PRIV-003) removes a NON-asset object — a completed export's
        // produced artifact — by its recorded bucket/key. It hands back no URL and only ever removes access.
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);

        await storage.DeleteObjectAsync("livecore-private-assets", "exports/org/ws/job.bin", CancellationToken.None);

        var request = Assert.Single(client.Deletes);
        Assert.Equal("livecore-private-assets", request.BucketName);
        Assert.Equal("exports/org/ws/job.bin", request.Key);
    }

    [Fact]
    public async Task DeleteObjectAsync_by_coordinates_rejects_blank_coordinates()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.DeleteObjectAsync(" ", "key", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.DeleteObjectAsync("bucket", " ", CancellationToken.None));
        Assert.Empty(client.Deletes);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_rejects_a_null_asset()
    {
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.CreateUploadUrlAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// A real AWS SDK S3 client (so pre-signed URL signing runs through the genuine SigV4 path, locally and
    /// offline) that RECORDS delete requests instead of performing the network call a real delete would make.
    /// Static, fake-but-well-formed credentials and a custom <c>ServiceURL</c> let the SDK sign without ever
    /// reaching a backend.
    /// </summary>
    private sealed class RecordingAmazonS3Client : AmazonS3Client
    {
        public RecordingAmazonS3Client()
            : base(
                new BasicAWSCredentials("test-access-key", "test-secret-key"),
                new AmazonS3Config
                {
                    ServiceURL = _endpoint,
                    ForcePathStyle = true,
                    AuthenticationRegion = "us-east-1",
                })
        {
        }

        public List<DeleteObjectRequest> Deletes { get; } = [];

        public override Task<DeleteObjectResponse> DeleteObjectAsync(
            DeleteObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            Deletes.Add(request);
            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the URL's issue/expiry instants are deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _fixedNow = now;

        public override DateTimeOffset GetUtcNow() => _fixedNow;
    }
}
