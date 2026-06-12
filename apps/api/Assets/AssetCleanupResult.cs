namespace LiveCore.Api.Assets;

/// <summary>
/// The outcome of one run of the background asset cleanup sweep (CORE-AST-006). It records identifiers and
/// counts only — never any storage coordinate — so the worker can log a sweep summary safely (threat T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
/// <param name="Examined">How many expired pending assets the sweep looked at this run.</param>
/// <param name="Removed">
/// How many assets were fully reclaimed: their storage object was deleted and then their metadata row was
/// removed. Object deletion always precedes row removal, so a removed asset never leaves an orphaned object.
/// </param>
/// <param name="Failed">
/// How many assets could not be reclaimed this run because deleting their storage object failed
/// transiently. Their metadata rows are intentionally KEPT so the next sweep retries them; nothing is left
/// in an inconsistent state.
/// </param>
/// <param name="StorageUnavailable">
/// <see langword="true"/> when the sweep stopped early because no object storage is configured (the
/// fail-closed <see cref="UnconfiguredAssetStorage"/>). In that case NO metadata row is removed, so an
/// object that storage cannot reach is never orphaned by deleting its row.
/// </param>
public readonly record struct AssetCleanupResult(
    int Examined,
    int Removed,
    int Failed,
    bool StorageUnavailable);
