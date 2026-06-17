using LiveCore.Api.Assets;
using LiveCore.Api.Observability;

namespace LiveCore.Api.UnitTests.Observability;

/// <summary>
/// Tests for <see cref="MeteredAssetStorage"/> (CORE-OBS-001), the transparent decorator that counts
/// upload/download failures of a CONFIGURED storage backend (docs/15_OBSERVABILITY.md "asset upload/download
/// failures"). They pin that it is genuinely transparent (a success returns the inner result and records
/// nothing), that a real backend failure is counted (tagged by operation) and rethrown, and — critically —
/// that the fail-closed <see cref="AssetStorageNotConfiguredException"/> is NOT counted (it is expected
/// private-by-default behavior, not a backend fault; threat T4) yet still propagates unchanged. Delete is
/// delegated transparently and is not part of the upload/download signal.
///
/// Shares the serialized "LiveCore metrics" collection so the listener sees only this test's measurements.
/// </summary>
[Collection(LiveCoreMetricsTestCollection.Name)]
public sealed class MeteredAssetStorageTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 13, 10, 0, 0, TimeSpan.Zero);

    private static Asset SampleAsset()
        => Asset.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "s3",
            "livecore-private-assets",
            $"assets/{Guid.NewGuid()}",
            "image/png",
            _now);

    private static SignedAssetUrl SampleUrl()
        => SignedAssetUrl.Create(
            new Uri("https://storage.example.com/object?sig=x", UriKind.Absolute),
            AssetStorageOperation.Download,
            _now,
            TimeSpan.FromMinutes(5));

    [Fact]
    public async Task A_successful_operation_returns_the_inner_result_and_records_nothing()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        var url = SampleUrl();
        var storage = new MeteredAssetStorage(new FakeStorage { UploadResult = url }, metrics);

        var result = await storage.CreateUploadUrlAsync(SampleAsset(), CancellationToken.None);

        Assert.Same(url, result);
        Assert.Equal(0, recorded.Count("livecore.asset.failures"));
    }

    [Fact]
    public async Task A_backend_failure_is_counted_by_operation_and_rethrown()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        var storage = new MeteredAssetStorage(
            new FakeStorage { DownloadError = new InvalidOperationException("backend 500") }, metrics);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.CreateDownloadUrlAsync(SampleAsset(), CancellationToken.None));

        Assert.Equal(1, recorded.Count("livecore.asset.failures"));
        Assert.True(recorded.HasTag("livecore.asset.failures", "operation", "download"));
    }

    [Fact]
    public async Task The_fail_closed_unconfigured_exception_is_not_counted_but_still_propagates()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        var storage = new MeteredAssetStorage(
            new FakeStorage { UploadError = new AssetStorageNotConfiguredException(AssetStorageOperation.Upload) },
            metrics);

        await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(
            () => storage.CreateUploadUrlAsync(SampleAsset(), CancellationToken.None));

        // Unconfigured storage is expected fail-closed behavior, not a backend failure.
        Assert.Equal(0, recorded.Count("livecore.asset.failures"));
    }

    [Fact]
    public async Task Delete_is_delegated_transparently_and_not_part_of_the_upload_download_signal()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        var inner = new FakeStorage();
        var storage = new MeteredAssetStorage(inner, metrics);

        await storage.DeleteObjectAsync(SampleAsset(), CancellationToken.None);

        Assert.True(inner.DeleteCalled);
        Assert.Equal(0, recorded.Count("livecore.asset.failures"));
    }

    [Fact]
    public async Task Coordinate_delete_is_delegated_transparently_and_not_part_of_the_upload_download_signal()
    {
        using var metrics = new LiveCoreMetrics();
        using var recorded = new RecordedMetrics();
        var inner = new FakeStorage();
        var storage = new MeteredAssetStorage(inner, metrics);

        await storage.DeleteObjectAsync("livecore-private-assets", "org/ws/export-artifact.bin", CancellationToken.None);

        Assert.True(inner.CoordinateDeleteCalled);
        Assert.Equal(0, recorded.Count("livecore.asset.failures"));
    }

    private sealed class FakeStorage : IAssetStorage
    {
        public SignedAssetUrl? UploadResult { get; init; }

        public SignedAssetUrl? DownloadResult { get; init; }

        public Exception? UploadError { get; init; }

        public Exception? DownloadError { get; init; }

        public bool DeleteCalled { get; private set; }

        public bool CoordinateDeleteCalled { get; private set; }

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => UploadError is not null
                ? Task.FromException<SignedAssetUrl>(UploadError)
                : Task.FromResult(UploadResult!);

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => DownloadError is not null
                ? Task.FromException<SignedAssetUrl>(DownloadError)
                : Task.FromResult(DownloadResult!);

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        {
            CoordinateDeleteCalled = true;
            return Task.CompletedTask;
        }
    }
}
