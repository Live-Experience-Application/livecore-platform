namespace LiveCore.Api.Assets;

/// <summary>
/// The asset cleanup job's application service (CORE-AST-006, the cleanup-job story of the "Asset Storage
/// and Authorization" epic). It reclaims ABANDONED upload intents: assets that were registered
/// <see cref="AssetStatus.Pending"/> when their upload intent was created (CORE-AST-003) but whose upload was
/// never confirmed (CORE-AST-004) within the deployment's grace window (<see cref="AssetCleanupOptions.PendingRetention"/>).
/// Each such asset leaves a stale metadata row and, possibly, an orphaned object in private object storage;
/// this job deletes both. The scheduling host — the background worker — invokes this service on an interval
/// (docs/02_ARCHITECTURE.md: the worker owns "cleanup" and async jobs); the service itself is host-agnostic
/// and fully unit-testable.
///
/// <para>
/// SECURITY POSTURE. Cleanup only ever REMOVES; it never serves bytes and never widens access, so it cannot
/// weaken the epic's private-by-default guarantee ("Assets are private by default and accessed only through
/// authorized signed URLs"; threat T4 "Asset leak" in docs/07_SECURITY_THREAT_MODEL.md). Three invariants
/// keep it safe:
/// </para>
/// <list type="bullet">
///   <item>
///   ONLY PENDING ASSETS ARE TOUCHED. The candidate query returns only <see cref="AssetStatus.Pending"/>
///   assets, and the loop re-checks each asset's status before acting, so a confirmed
///   (<see cref="AssetStatus.Available"/>) asset — real, possibly-linked content — is NEVER deleted, even if
///   it is old.
///   </item>
///   <item>
///   OBJECT FIRST, THEN ROW. The storage object is deleted BEFORE the metadata row, so a row is never
///   removed while its object remains: cleanup never produces an orphaned object. If object deletion fails
///   transiently, the row is kept and the next sweep retries it.
///   </item>
///   <item>
///   FAIL CLOSED WHEN STORAGE IS UNCONFIGURED. If the fail-closed <see cref="UnconfiguredAssetStorage"/> is
///   in place, object deletion throws and the sweep stops having removed nothing — it never deletes a
///   metadata row whose object it could not delete (mirrors how the upload/download flows fail closed when
///   storage is not configured; and per CORE-AST-003 no pending asset can even exist while storage is
///   unconfigured).
///   </item>
/// </list>
///
/// All logging is identifier-only (asset ids and counts, never a storage coordinate), so a sweep is safe to
/// log (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class ExpiredPendingAssetCleanupService
{
    private readonly IAssetRepository _assets;
    private readonly IAssetStorage _storage;
    private readonly AssetCleanupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExpiredPendingAssetCleanupService> _logger;

    /// <summary>Creates the cleanup service over the asset repository, the storage adapter and the cleanup policy.</summary>
    public ExpiredPendingAssetCleanupService(
        IAssetRepository assets,
        IAssetStorage storage,
        AssetCleanupOptions options,
        TimeProvider timeProvider,
        ILogger<ExpiredPendingAssetCleanupService> logger)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _assets = assets;
        _storage = storage;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs one cleanup sweep: finds up to <see cref="AssetCleanupOptions.BatchSize"/> expired pending assets
    /// (created longer than <see cref="AssetCleanupOptions.PendingRetention"/> ago), deletes each one's
    /// storage object and then its metadata row, and returns the run's identifier-only summary. The sweep is
    /// resilient: a transient per-asset storage failure is logged and that asset is left for the next sweep
    /// to retry, rather than aborting the whole run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (the worker passes its stopping token).</param>
    /// <returns>A summary of how many assets were examined, removed and failed, and whether storage was unavailable.</returns>
    public async Task<AssetCleanupResult> CleanUpExpiredPendingAssetsAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var createdBefore = now - _options.PendingRetention;

        var candidates = await _assets
            .ListExpiredPendingAsync(createdBefore, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return new AssetCleanupResult(Examined: 0, Removed: 0, Failed: 0, StorageUnavailable: false);
        }

        var examined = 0;
        var removed = 0;
        var failed = 0;

        foreach (var asset in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            // Defence in depth: the query already filters to pending, but never act on a confirmed asset.
            // Confirmed content is never reclaimed (threat T4).
            if (asset.Status != AssetStatus.Pending)
            {
                _logger.LogWarning(
                    "Skipping asset {AssetId} during cleanup: it is {Status}, not pending.",
                    asset.Id,
                    asset.Status);
                continue;
            }

            try
            {
                // Delete the storage object FIRST. Idempotent: a pending asset whose client never uploaded
                // has no object, and the adapter treats that as success.
                await _storage.DeleteObjectAsync(asset, cancellationToken).ConfigureAwait(false);
            }
            catch (AssetStorageNotConfiguredException)
            {
                // Fail closed: with no storage adapter we cannot delete objects, so we must not delete any
                // metadata rows (that would orphan objects once storage is configured). Stop the sweep
                // having removed nothing.
                _logger.LogWarning(
                    "Asset storage is not configured; stopping cleanup sweep with no assets removed.");
                return new AssetCleanupResult(
                    Examined: examined,
                    Removed: removed,
                    Failed: failed,
                    StorageUnavailable: true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A transient storage failure for one asset: keep its metadata row so the next sweep retries
                // it, and move on. The message carries only the asset id, never a storage coordinate (T7).
                failed++;
                _logger.LogWarning(
                    exception,
                    "Failed to delete the storage object for expired pending asset {AssetId}; keeping its record for retry.",
                    asset.Id);
                continue;
            }

            // The object is gone (or never existed); now remove the metadata row. Its links cascade away.
            await _assets.DeleteAsync(asset, cancellationToken).ConfigureAwait(false);
            removed++;
            _logger.LogInformation("Removed expired pending asset {AssetId}.", asset.Id);
        }

        return new AssetCleanupResult(
            Examined: examined,
            Removed: removed,
            Failed: failed,
            StorageUnavailable: false);
    }
}
