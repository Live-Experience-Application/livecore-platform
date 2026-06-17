// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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

    private static Asset CreateAsset(string objectKey, string contentType, long sizeBytes)
        => Asset.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "s3",
            _bucket,
            objectKey,
            contentType,
            _now,
            sizeBytes);

    /// <summary>Reads a single query-string parameter from a pre-signed URL (its raw, still-escaped value).</summary>
    private static string QueryValue(Uri url, string name)
    {
        var prefix = name + "=";
        var pair = url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.Ordinal));
        return pair is null ? string.Empty : pair[prefix.Length..];
    }

    /// <summary>
    /// The decoded set of headers a pre-signed URL signs (its <c>X-Amz-SignedHeaders</c> query value),
    /// lower-cased. A header in this set is part of the SigV4 signature, so the storage backend rejects a
    /// request whose corresponding header differs from the signed value.
    /// </summary>
    private static IReadOnlySet<string> SignedHeaders(Uri url)
        => Uri.UnescapeDataString(QueryValue(url, "X-Amz-SignedHeaders"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static header => header.ToLowerInvariant())
            .ToHashSet();

    /// <summary>The SigV4 signature a pre-signed URL carries (its <c>X-Amz-Signature</c> query value).</summary>
    private static string Signature(Uri url) => QueryValue(url, "X-Amz-Signature");

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

    // =====================================================================
    // CORE-AST-008 — the upload URL BINDS the declared content type and content length as SigV4 signed
    // conditions, so the storage backend itself rejects an upload whose actual type or byte count violates what
    // was declared at upload-intent. A download URL is unchanged (it serves an already-stored object).
    // =====================================================================

    [Fact]
    public async Task CreateUploadUrlAsync_binds_the_declared_content_type_and_content_length_as_signed_conditions()
    {
        // The asset declares image/png and an exact size; the upload URL must SIGN both content-type and
        // content-length, so an upload with a different type or a different (in particular larger) byte count
        // fails the SigV4 signature check at storage and is rejected — closing the declare-small / upload-large
        // quota-and-ceiling bypass (CORE-AST-008).
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/bound-upload.bin", "image/png", sizeBytes: 12_345);

        var signed = await storage.CreateUploadUrlAsync(asset, CancellationToken.None);

        var headers = SignedHeaders(signed.Url);
        Assert.Contains("content-type", headers);
        Assert.Contains("content-length", headers);
        // Still a genuine, asset-scoped, short-lived signed URL — the binding only adds signed conditions.
        Assert.Equal(AssetStorageOperation.Upload, signed.Operation);
        Assert.Contains("X-Amz-Signature", signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(signed.IsExpired(_now));
        Assert.True(signed.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_signature_is_bound_to_the_declared_size_so_a_larger_upload_cannot_reuse_it()
    {
        // Two intents for the SAME object that differ ONLY in declared size produce DIFFERENT signatures: the
        // content length is part of the signature, so a URL minted for N bytes can never be replayed to PUT more
        // than N bytes (the quota/ceiling bypass this story closes). The shared object key isolates the size as
        // the only difference.
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var small = CreateAsset("org/ws/same-object.bin", "image/png", sizeBytes: 1);
        var large = CreateAsset("org/ws/same-object.bin", "image/png", sizeBytes: 1_000_000_000);

        var smallUrl = await storage.CreateUploadUrlAsync(small, CancellationToken.None);
        var largeUrl = await storage.CreateUploadUrlAsync(large, CancellationToken.None);

        Assert.NotEqual(Signature(smallUrl.Url), Signature(largeUrl.Url));
    }

    [Fact]
    public async Task CreateUploadUrlAsync_signature_is_bound_to_the_declared_content_type_so_another_type_cannot_reuse_it()
    {
        // Two intents for the SAME object and size that differ ONLY in declared content type produce DIFFERENT
        // signatures: the content type is part of the signature, so a URL minted for image/png can never be
        // replayed to PUT an object of a different (possibly disallowed) type.
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var png = CreateAsset("org/ws/same-object.bin", "image/png", sizeBytes: 512);
        var zip = CreateAsset("org/ws/same-object.bin", "application/zip", sizeBytes: 512);

        var pngUrl = await storage.CreateUploadUrlAsync(png, CancellationToken.None);
        var zipUrl = await storage.CreateUploadUrlAsync(zip, CancellationToken.None);

        Assert.NotEqual(Signature(pngUrl.Url), Signature(zipUrl.Url));
    }

    [Fact]
    public async Task CreateUploadUrlAsync_in_spec_declared_upload_still_mints_a_well_formed_short_lived_url()
    {
        // The in-spec upload still succeeds: a declared-content-type-and-size intent mints an absolute,
        // asset-scoped, not-yet-expired signed URL within the lifetime ceiling (the story's "an in-spec upload
        // still succeeds").
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/in-spec.bin", "application/pdf", sizeBytes: 2_048);

        var signed = await storage.CreateUploadUrlAsync(asset, CancellationToken.None);

        Assert.Equal(AssetStorageOperation.Upload, signed.Operation);
        Assert.True(signed.Url.IsAbsoluteUri);
        Assert.StartsWith(_endpoint, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(asset.ObjectKey, signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature", signed.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(signed.IsExpired(_now));
        Assert.Equal(_now + _options.UrlLifetime, signed.ExpiresAt);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_does_not_bind_content_type_or_content_length()
    {
        // A download serves an already-stored object, so there is nothing to constrain: the download URL must
        // NOT sign content-type/content-length (over-constraining it would needlessly break legitimate
        // downloads). The CORE-AST-008 binding applies to the UPLOAD direction only.
        using var client = new RecordingAmazonS3Client();
        var storage = CreateStorage(client);
        var asset = CreateAsset("org/ws/download-unbound.bin", "image/png", sizeBytes: 12_345);

        var signed = await storage.CreateDownloadUrlAsync(asset, CancellationToken.None);

        var headers = SignedHeaders(signed.Url);
        Assert.DoesNotContain("content-type", headers);
        Assert.DoesNotContain("content-length", headers);
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
