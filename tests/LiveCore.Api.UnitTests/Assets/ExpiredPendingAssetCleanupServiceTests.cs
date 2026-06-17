using LiveCore.Api.Assets;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="ExpiredPendingAssetCleanupService"/> (CORE-AST-006) — the background asset
/// cleanup job's application service, which reclaims abandoned, unconfirmed (pending) upload intents older
/// than the deployment's grace window. The service is driven over a minimal in-memory
/// <see cref="IAssetRepository"/> and a configurable fake <see cref="IAssetStorage"/>, so the tests assert
/// the job's own behavior without persistence or a real signer.
///
/// The security-relevant invariants — the cleanup job only ever REMOVES access, never widens it (the epic
/// acceptance criterion; threat T4 "Asset leak" in docs/07_SECURITY_THREAT_MODEL.md) — are exercised
/// directly:
/// <list type="bullet">
///   <item>Expired pending assets are reclaimed: the storage object is deleted BEFORE the metadata row, so
///   a row never outlives its object.</item>
///   <item>FAIL-CLOSED: when no storage adapter is configured the sweep removes NOTHING — it never deletes a
///   metadata row whose object it could not delete.</item>
///   <item>A confirmed (available) asset is NEVER deleted, even if it appears in the candidate list (defence
///   in depth): real content is never reclaimed.</item>
///   <item>A transient per-asset storage failure keeps that asset's row for the next sweep and does not
///   abort the run.</item>
/// </list>
/// All fixtures are generic (AGENTS.md, csv/forbidden_core_terms.csv).
/// </summary>
public class ExpiredPendingAssetCleanupServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AssetCleanupOptions _options =
        new(TimeSpan.FromHours(24), TimeSpan.FromHours(1), 100);

    [Fact]
    public async Task CleanUp_removes_expired_pending_assets_deleting_the_object_before_the_row()
    {
        var first = CreatePending(_now - TimeSpan.FromDays(3));
        var second = CreatePending(_now - TimeSpan.FromDays(2));
        var repository = new RecordingAssetRepository(first, second);
        // The fake verifies the row still exists when the object is deleted, proving object-before-row.
        var storage = new ConfigurableAssetStorage(asset =>
        {
            Assert.True(repository.Contains(asset.Id));
            return null;
        });
        var service = CreateService(repository, storage);

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Failed);
        Assert.False(result.StorageUnavailable);
        Assert.Equal(new[] { first.Id, second.Id }, storage.DeletedObjects);
        Assert.Equal(new[] { first.Id, second.Id }, repository.Deleted);
    }

    [Fact]
    public async Task CleanUp_computes_the_threshold_from_the_retention_window_and_passes_the_batch_size()
    {
        var repository = new RecordingAssetRepository();
        var options = new AssetCleanupOptions(TimeSpan.FromHours(24), TimeSpan.FromHours(1), 50);
        var service = new ExpiredPendingAssetCleanupService(
            repository, new ConfigurableAssetStorage(), options, new FixedTimeProvider(_now),
            NullLogger<ExpiredPendingAssetCleanupService>.Instance);

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        // Eligible only once pending for longer than the retention window: createdBefore = now - retention.
        Assert.Equal(_now - TimeSpan.FromHours(24), repository.LastCreatedBefore);
        Assert.Equal(50, repository.LastMaxCount);
        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public async Task CleanUp_does_nothing_when_there_are_no_expired_pending_assets()
    {
        var repository = new RecordingAssetRepository();
        var storage = new ConfigurableAssetStorage(_ =>
            throw new InvalidOperationException("storage must not be touched when there is nothing to clean up"));
        var service = CreateService(repository, storage);

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        Assert.Equal(0, result.Examined);
        Assert.Equal(0, result.Removed);
        Assert.Empty(storage.DeletedObjects);
        Assert.Empty(repository.Deleted);
    }

    [Fact]
    public async Task CleanUp_fails_closed_and_removes_nothing_when_storage_is_unconfigured()
    {
        // The crown-jewel negative test: with the production fail-closed adapter, deleting an object throws,
        // so the sweep must remove NO metadata rows — a row is never deleted while its object could not be
        // (which would orphan the object once storage is wired). Private-by-default holds even unconfigured.
        var first = CreatePending(_now - TimeSpan.FromDays(3));
        var second = CreatePending(_now - TimeSpan.FromDays(2));
        var repository = new RecordingAssetRepository(first, second);
        var service = CreateService(repository, new UnconfiguredAssetStorage());

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        Assert.True(result.StorageUnavailable);
        Assert.Equal(0, result.Removed);
        Assert.Empty(repository.Deleted);
    }

    [Fact]
    public async Task CleanUp_never_deletes_a_confirmed_asset()
    {
        // Defence in depth: even if a confirmed (available) asset somehow appears in the candidate list, the
        // service must skip it — confirmed content (possibly linked and audience-visible) is never reclaimed
        // (threat T4). The storage object is never deleted and the row is never removed.
        var confirmed = CreateAvailable(_now - TimeSpan.FromDays(3), _now - TimeSpan.FromDays(2));
        var repository = new RecordingAssetRepository(confirmed);
        var storage = new ConfigurableAssetStorage();
        var service = CreateService(repository, storage);

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        Assert.Equal(1, result.Examined);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Failed);
        Assert.Empty(storage.DeletedObjects);
        Assert.Empty(repository.Deleted);
    }

    [Fact]
    public async Task CleanUp_keeps_the_row_and_continues_when_object_deletion_fails_transiently()
    {
        // A transient storage error for one asset must NOT abort the sweep and must KEEP that asset's row so
        // the next sweep retries it; the other assets are still reclaimed.
        var failing = CreatePending(_now - TimeSpan.FromDays(3));
        var succeeding = CreatePending(_now - TimeSpan.FromDays(2));
        var repository = new RecordingAssetRepository(failing, succeeding);
        var storage = new ConfigurableAssetStorage(asset =>
            asset.Id == failing.Id ? new IOException("transient storage failure") : null);
        var service = CreateService(repository, storage);

        var result = await service.CleanUpExpiredPendingAssetsAsync(CancellationToken.None);

        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Failed);
        Assert.False(result.StorageUnavailable);
        // The failing asset's row is kept (not deleted); only the succeeding asset is reclaimed.
        Assert.Equal(new[] { succeeding.Id }, repository.Deleted);
        Assert.Equal(new[] { succeeding.Id }, storage.DeletedObjects);
        Assert.True(repository.Contains(failing.Id));
    }

    private static ExpiredPendingAssetCleanupService CreateService(
        IAssetRepository repository, IAssetStorage storage)
        => new(
            repository,
            storage,
            _options,
            new FixedTimeProvider(_now),
            NullLogger<ExpiredPendingAssetCleanupService>.Instance);

    private static Asset CreatePending(DateTimeOffset createdAt)
        => Asset.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "s3",
            "livecore-private-assets",
            $"org/ws/{Guid.CreateVersion7():N}.bin",
            "image/png",
            createdAt);

    private static Asset CreateAvailable(DateTimeOffset createdAt, DateTimeOffset confirmedAt)
    {
        var asset = CreatePending(createdAt);
        asset.MarkAvailable(1024, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", confirmedAt);
        return asset;
    }

    /// <summary>
    /// A minimal in-memory <see cref="IAssetRepository"/> that returns a fixed candidate list and records the
    /// sweep's query parameters and the assets deleted through it. Only <see cref="ListExpiredPendingAsync"/>
    /// and <see cref="DeleteAsync"/> are exercised by the cleanup service; the other members throw.
    /// </summary>
    private sealed class RecordingAssetRepository : IAssetRepository
    {
        private readonly List<Asset> _pending;

        public RecordingAssetRepository(params Asset[] pending) => _pending = [.. pending];

        public DateTimeOffset? LastCreatedBefore { get; private set; }

        public int? LastMaxCount { get; private set; }

        public List<Guid> Deleted { get; } = [];

        public bool Contains(Guid id) => _pending.Exists(asset => asset.Id == id);

        public Task<IReadOnlyList<Asset>> ListExpiredPendingAsync(
            DateTimeOffset createdBefore, int maxCount, CancellationToken cancellationToken)
        {
            LastCreatedBefore = createdBefore;
            LastMaxCount = maxCount;
            IReadOnlyList<Asset> batch = _pending.Take(maxCount).ToList();
            return Task.FromResult(batch);
        }

        public Task DeleteAsync(Asset asset, CancellationToken cancellationToken)
        {
            Deleted.Add(asset.Id);
            _pending.RemoveAll(stored => stored.Id == asset.Id);
            return Task.CompletedTask;
        }

        public Task<Asset?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Asset?> FindByIdInOrganizationAsync(Guid organizationId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Asset>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<AssetAddResult> AddAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A configurable in-memory <see cref="IAssetStorage"/> for the cleanup tests. Its
    /// <see cref="DeleteObjectAsync(Asset, System.Threading.CancellationToken)"/> records the deleted asset ids and can be made to throw per asset (via
    /// the supplied callback) to simulate a transient failure. The signing operations are not exercised here.
    /// </summary>
    private sealed class ConfigurableAssetStorage(Func<Asset, Exception?>? onDelete = null) : IAssetStorage
    {
        private readonly Func<Asset, Exception?> _onDelete = onDelete ?? (_ => null);

        public List<Guid> DeletedObjects { get; } = [];

        public Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var failure = _onDelete(asset);
            if (failure is not null)
            {
                throw failure;
            }

            DeletedObjects.Add(asset.Id);
            return Task.CompletedTask;
        }

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(string bucket, string objectKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A fixed <see cref="TimeProvider"/> so the sweep's "now" (and the derived threshold) is deterministic.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
