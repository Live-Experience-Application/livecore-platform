using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for the fail-closed default storage adapter <see cref="UnconfiguredAssetStorage"/>
/// (CORE-AST-002). When no concrete S3-compatible adapter is configured, asset access must FAIL CLOSED:
/// no signed URL is ever produced, so assets stay private by default rather than being served some
/// insecure way (the epic acceptance criterion; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). These are the negative, security-relevant cases for the adapter
/// seam — both upload and download deny, and the thrown exception is log-safe (it names only the
/// operation, never a storage coordinate; threat T7). The concrete provider adapter and its
/// endpoint/credentials are supplied by the deployment (docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006).
/// Generic Asset vocabulary only (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class UnconfiguredAssetStorageTests
{
    private static readonly Asset _asset = Asset.Create(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "s3",
        "livecore-private-assets",
        "org/ws/2026/06/12/asset-001.bin",
        "image/png",
        new DateTimeOffset(2026, 6, 12, 8, 0, 0, TimeSpan.Zero));

    private static readonly IAssetStorage _storage = new UnconfiguredAssetStorage();

    [Fact]
    public async Task CreateUploadUrlAsync_fails_closed_when_storage_is_not_configured()
    {
        var exception = await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(
            () => _storage.CreateUploadUrlAsync(_asset, CancellationToken.None));

        Assert.Equal(AssetStorageOperation.Upload, exception.Operation);
    }

    [Fact]
    public async Task CreateDownloadUrlAsync_fails_closed_when_storage_is_not_configured()
    {
        var exception = await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(
            () => _storage.CreateDownloadUrlAsync(_asset, CancellationToken.None));

        Assert.Equal(AssetStorageOperation.Download, exception.Operation);
    }

    [Fact]
    public async Task The_fail_closed_exception_message_leaks_no_storage_coordinate()
    {
        var exception = await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(
            () => _storage.CreateDownloadUrlAsync(_asset, CancellationToken.None));

        // The message must be safe for structured logs: it carries only the operation name, never the
        // bucket, object key, provider or any other coordinate that could help locate private content
        // (threats T4/T7 in docs/07_SECURITY_THREAT_MODEL.md).
        Assert.DoesNotContain(_asset.Bucket, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_asset.ObjectKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_asset.StorageProvider, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateUploadUrlAsync_rejects_a_null_asset()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => _storage.CreateUploadUrlAsync(null!, CancellationToken.None));

    [Fact]
    public async Task CreateDownloadUrlAsync_rejects_a_null_asset()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            () => _storage.CreateDownloadUrlAsync(null!, CancellationToken.None));
}
