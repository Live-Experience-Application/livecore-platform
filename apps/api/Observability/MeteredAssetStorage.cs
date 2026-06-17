// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Assets;

namespace LiveCore.Api.Observability;

/// <summary>
/// A transparent <see cref="IAssetStorage"/> decorator that counts upload/download failures of a CONFIGURED
/// storage backend onto <see cref="LiveCoreMetrics"/> (CORE-OBS-001) — the docs/15_OBSERVABILITY.md "asset
/// upload/download failures" signal. It delegates every call unchanged to the wrapped adapter (the concrete
/// S3-compatible adapter or the fail-closed default) and adds ONLY a failure count, so it never alters the
/// security-critical behavior of the storage seam: the same signed URLs, the same fail-closed exceptions, the
/// same delete.
///
/// It distinguishes a BACKEND FAILURE from the fail-closed UNCONFIGURED state. When no storage is configured
/// every operation throws <see cref="AssetStorageNotConfiguredException"/> by design — that is expected,
/// private-by-default behavior (threat T4), not an operational fault — so the decorator rethrows it WITHOUT
/// counting. Any other failure (a real S3-compatible backend erroring) is counted, tagged by operation, and
/// rethrown so the caller's behavior is identical.
///
/// Only the two client-facing operations (<see cref="CreateUploadUrlAsync"/>/<see cref="CreateDownloadUrlAsync"/>)
/// are the documented "upload/download" signal; <see cref="DeleteObjectAsync(Asset, System.Threading.CancellationToken)"/> (the background cleanup
/// delete) is delegated transparently and is surfaced instead through the worker's background-job-failure
/// metric.
/// </summary>
internal sealed class MeteredAssetStorage : IAssetStorage
{
    private const string _uploadOperation = "upload";
    private const string _downloadOperation = "download";

    private readonly IAssetStorage _inner;
    private readonly LiveCoreMetrics _metrics;

    public MeteredAssetStorage(IAssetStorage inner, LiveCoreMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(metrics);
        _inner = inner;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        => MeasureAsync(_uploadOperation, _inner.CreateUploadUrlAsync(asset, cancellationToken));

    /// <inheritdoc />
    public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        => MeasureAsync(_downloadOperation, _inner.CreateDownloadUrlAsync(asset, cancellationToken));

    /// <inheritdoc />
    public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        => _inner.DeleteObjectAsync(asset, cancellationToken);

    /// <inheritdoc />
    public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => _inner.DeleteObjectAsync(bucket, objectKey, cancellationToken);

    /// <summary>
    /// Awaits the wrapped operation and, on a backend failure (anything other than the fail-closed
    /// <see cref="AssetStorageNotConfiguredException"/>), records one asset failure for
    /// <paramref name="operation"/> before rethrowing, so the caller's outcome is unchanged.
    /// </summary>
    private async Task<SignedAssetUrl> MeasureAsync(string operation, Task<SignedAssetUrl> operationTask)
    {
        try
        {
            return await operationTask.ConfigureAwait(false);
        }
        catch (AssetStorageNotConfiguredException)
        {
            // Fail-closed unconfigured storage is expected behavior, not a backend failure; do not count it.
            throw;
        }
        catch
        {
            _metrics.RecordAssetFailure(operation);
            throw;
        }
    }
}
