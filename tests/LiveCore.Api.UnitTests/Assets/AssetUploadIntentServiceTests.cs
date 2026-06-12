using LiveCore.Api.Assets;

namespace LiveCore.Api.UnitTests.Assets;

/// <summary>
/// Unit tests for <see cref="AssetUploadIntentService"/> (CORE-AST-003) — the upload-intent command that
/// registers a PENDING <see cref="Asset"/> with server-minted storage coordinates and mints its
/// short-lived signed upload URL through the <see cref="IAssetStorage"/> adapter port (CORE-AST-002). The
/// service is driven over a minimal in-memory <see cref="IAssetRepository"/> and a conforming fake storage
/// adapter (the deployment supplies the real S3-compatible signer), so the test asserts the command's own
/// behavior without persistence or a real signer.
///
/// These are the storage-adapter half of the story's required "Upload/download auth tests and storage
/// adapter tests":
/// <list type="bullet">
///   <item>The asset is registered PENDING with the deployment's provider/bucket, a tenant- and
///   workspace-scoped object key, the declared content type and the authenticated creator; the signed URL
///   is an <see cref="AssetStorageOperation.Upload"/> URL for the asset's OWN coordinates, short-lived and
///   not expired at issue (private by default; threat T4 "Asset leak").</item>
///   <item>FAIL-CLOSED: when storage is unconfigured the command throws
///   <see cref="AssetStorageNotConfiguredException"/> and persists NOTHING (no orphan pending asset).</item>
///   <item>Two intents mint DISTINCT object keys, so they never alias the same stored object.</item>
///   <item>Input guards reject empty tenant/workspace/creator ids and an invalid content type.</item>
/// </list>
/// All fixtures are generic (AGENTS.md).
/// </summary>
public class AssetUploadIntentServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _organizationId = Guid.CreateVersion7();
    private static readonly Guid _workspaceId = Guid.CreateVersion7();
    private static readonly Guid _createdBy = Guid.CreateVersion7();

    private static readonly AssetStorageLocation _location =
        new("s3", "livecore-private-assets");

    [Fact]
    public async Task CreateAsync_registers_a_pending_asset_with_server_minted_coordinates()
    {
        var repository = new InMemoryAssetRepository();
        var service = new AssetUploadIntentService(repository, new FakeSignedUrlAssetStorage(_now), _location);

        var intent = await service.CreateAsync(
            _organizationId, _workspaceId, _createdBy, "image/png", _now, CancellationToken.None);

        var asset = intent.Asset;
        Assert.Equal(_organizationId, asset.OrganizationId);
        Assert.Equal(_workspaceId, asset.WorkspaceId);
        Assert.Equal(_createdBy, asset.CreatedByUserProfileId);
        Assert.Equal("s3", asset.StorageProvider);
        Assert.Equal("livecore-private-assets", asset.Bucket);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(AssetStatus.Pending, asset.Status);
        Assert.Null(asset.SizeBytes);
        Assert.Null(asset.Checksum);

        // The object key is tenant- and workspace-scoped (it leads with the organization then the
        // workspace), so it can only ever address an object inside the caller's own tenant/workspace.
        Assert.StartsWith($"assets/{_organizationId}/{_workspaceId}/", asset.ObjectKey, StringComparison.Ordinal);

        // The asset metadata row was persisted exactly once.
        Assert.Single(repository.Added);
        Assert.Same(asset, repository.Added[0]);
    }

    [Fact]
    public async Task CreateAsync_mints_a_short_lived_upload_url_for_the_assets_own_object()
    {
        var repository = new InMemoryAssetRepository();
        var service = new AssetUploadIntentService(repository, new FakeSignedUrlAssetStorage(_now), _location);

        var intent = await service.CreateAsync(
            _organizationId, _workspaceId, _createdBy, "application/pdf", _now, CancellationToken.None);

        var url = intent.UploadUrl;
        Assert.Equal(AssetStorageOperation.Upload, url.Operation);
        Assert.True(url.Url.IsAbsoluteUri);
        // Short-lived and still valid at issue (threat T4; docs/12_STORAGE_ASSETS.md "signed URLs are
        // short-lived").
        Assert.False(url.IsExpired(_now));
        Assert.True(url.ExpiresAt <= _now + SignedAssetUrl.MaxLifetime);
        // Signed for THIS asset's own coordinates, not an arbitrary bucket/object (threats T5/T1).
        Assert.Contains(intent.Asset.Bucket, url.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(intent.Asset.ObjectKey, url.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_fails_closed_and_persists_nothing_when_storage_is_unconfigured()
    {
        var repository = new InMemoryAssetRepository();
        // The production fail-closed default adapter: no object storage configured.
        var service = new AssetUploadIntentService(repository, new UnconfiguredAssetStorage(), _location);

        await Assert.ThrowsAsync<AssetStorageNotConfiguredException>(() => service.CreateAsync(
            _organizationId, _workspaceId, _createdBy, "image/png", _now, CancellationToken.None));

        // No orphan pending asset: the URL is minted before the row is persisted, so a fail-closed storage
        // error leaves no side effect (private-by-default holds even unconfigured; threat T4).
        Assert.Empty(repository.Added);
    }

    [Fact]
    public async Task CreateAsync_mints_distinct_object_keys_for_two_intents()
    {
        var repository = new InMemoryAssetRepository();
        var service = new AssetUploadIntentService(repository, new FakeSignedUrlAssetStorage(_now), _location);

        var first = await service.CreateAsync(_organizationId, _workspaceId, _createdBy, "image/png", _now, CancellationToken.None);
        var second = await service.CreateAsync(_organizationId, _workspaceId, _createdBy, "image/png", _now, CancellationToken.None);

        Assert.NotEqual(first.Asset.ObjectKey, second.Asset.ObjectKey);
        Assert.NotEqual(first.Asset.Id, second.Asset.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a content type")]
    public async Task CreateAsync_rejects_an_invalid_content_type(string contentType)
    {
        var repository = new InMemoryAssetRepository();
        var service = new AssetUploadIntentService(repository, new FakeSignedUrlAssetStorage(_now), _location);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            _organizationId, _workspaceId, _createdBy, contentType, _now, CancellationToken.None));

        Assert.Empty(repository.Added);
    }

    [Fact]
    public async Task CreateAsync_rejects_empty_identity_inputs()
    {
        var repository = new InMemoryAssetRepository();
        var service = new AssetUploadIntentService(repository, new FakeSignedUrlAssetStorage(_now), _location);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            Guid.Empty, _workspaceId, _createdBy, "image/png", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            _organizationId, Guid.Empty, _createdBy, "image/png", _now, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            _organizationId, _workspaceId, Guid.Empty, "image/png", _now, CancellationToken.None));

        Assert.Empty(repository.Added);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IAssetRepository"/> that records the assets added through it. Only
    /// <see cref="AddAsync"/> is exercised by the upload-intent service; the read/update members are not
    /// used here and throw if called.
    /// </summary>
    private sealed class InMemoryAssetRepository : IAssetRepository
    {
        public List<Asset> Added { get; } = [];

        public Task<AssetAddResult> AddAsync(Asset asset, CancellationToken cancellationToken)
        {
            Added.Add(asset);
            return Task.FromResult(AssetAddResult.Added);
        }

        public Task<Asset?> FindByIdAsync(Guid organizationId, Guid workspaceId, Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Asset>> ListByWorkspaceAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task UpdateAsync(Asset asset, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// A conforming in-memory <see cref="IAssetStorage"/> standing in for the deployment-supplied
    /// S3-compatible adapter: it mints a short-lived signed URL for the asset's own coordinates and can
    /// never produce a public or non-expiring URL because <see cref="SignedAssetUrl"/> makes that
    /// unrepresentable. Not a production signer.
    /// </summary>
    private sealed class FakeSignedUrlAssetStorage(DateTimeOffset now) : IAssetStorage
    {
        private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

        public Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=put&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Upload, now, _lifetime));
        }

        public Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(asset);
            var url = new Uri($"https://storage.example.com/{asset.Bucket}/{asset.ObjectKey}?op=get&signature=fake");
            return Task.FromResult(SignedAssetUrl.Create(url, AssetStorageOperation.Download, now, _lifetime));
        }
    }
}
