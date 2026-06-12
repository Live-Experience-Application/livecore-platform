namespace LiveCore.Api.Assets;

/// <summary>
/// The upload-intent command of the Assets module (CORE-AST-003) — the "Create upload intent" step of the
/// asset lifecycle (docs/12_STORAGE_ASSETS.md). It registers a new <see cref="AssetStatus.Pending"/>
/// <see cref="Asset"/> and mints the short-lived, signed URL the authorized client uploads the object with.
/// It is a plain command service over <see cref="IAssetRepository"/>, the <see cref="IAssetStorage"/>
/// adapter port (CORE-AST-002) and the deployment's <see cref="AssetStorageLocation"/>, taking explicit
/// already-resolved inputs, exactly like <see cref="LiveCore.Api.Visibility.RevealService"/>: the calling
/// endpoint resolves the tenant, the workspace and authorizes the caller's role BEFORE invoking it. This
/// service performs no authorization itself.
///
/// SERVER-MINTED COORDINATES. The storage coordinates are assigned here, never accepted from the client:
/// the <see cref="AssetStorageLocation.Provider"/> and <see cref="AssetStorageLocation.Bucket"/> are the
/// deployment's private naming, and the object key is minted by <see cref="BuildObjectKey"/> as a
/// tenant- and workspace-scoped, collision-free path (<c>assets/{organizationId}/{workspaceId}/{uuid}</c>,
/// the per-object segment a fresh time-ordered UUID). A client therefore can never choose the bucket or
/// point an upload at another tenant's or workspace's object, and two intents can never alias the same
/// stored object (the storage-object-key uniqueness guarantee the CORE-AST-001 notes deferred to this
/// flow; threats T5/T1).
///
/// PRIVATE BY DEFAULT, FAIL-CLOSED. The asset is created <see cref="AssetStatus.Pending"/> with no public
/// affordance (the epic acceptance criterion: "Assets are private by default and accessed only through
/// authorized signed URLs"; threat T4 "Asset leak"). The signed URL is minted through the storage adapter
/// BEFORE the metadata row is persisted, so when storage is not configured the fail-closed
/// <see cref="UnconfiguredAssetStorage"/> throws <see cref="AssetStorageNotConfiguredException"/> and NO
/// orphan pending asset is left behind — the command has no side effect at all. The signed URL is itself a
/// secret (it embeds the object key and signature); it is returned to the authorized caller only and never
/// logged (threats T4/T7; <see cref="SignedAssetUrl.ToString"/> and <see cref="Asset.ToString"/> both
/// exclude it / the coordinates).
/// </summary>
internal sealed class AssetUploadIntentService
{
    private readonly IAssetRepository _assets;
    private readonly IAssetStorage _storage;
    private readonly AssetStorageLocation _location;

    public AssetUploadIntentService(
        IAssetRepository assets,
        IAssetStorage storage,
        AssetStorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(location);
        _assets = assets;
        _storage = storage;
        _location = location;
    }

    /// <summary>
    /// Registers a new pending asset in the given workspace (owned by the given organization), created by
    /// the given authenticated user, with the client-declared content type, and mints its short-lived
    /// signed upload URL. The storage coordinates are assigned server-side. Returns the registered asset
    /// and the signed URL.
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the asset belongs to.</param>
    /// <param name="createdByUserProfileId">The authenticated user registering the asset (the audited creator).</param>
    /// <param name="contentType">The client-declared MIME content type of the object to be uploaded.</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or creator id is empty, or the content type is not a valid
    /// content-type token.
    /// </exception>
    /// <exception cref="AssetStorageNotConfiguredException">
    /// No object storage is configured; nothing is persisted (fail-closed).
    /// </exception>
    public async Task<AssetUploadIntent> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid createdByUserProfileId,
        string contentType,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id must not be empty.", nameof(organizationId));
        }

        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
        }

        if (createdByUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Created-by user profile id must not be empty.", nameof(createdByUserProfileId));
        }

        var normalizedContentType = contentType?.Trim() ?? string.Empty;
        if (!Asset.IsValidContentType(normalizedContentType))
        {
            throw new ArgumentException("Content type violates the content type invariants.", nameof(contentType));
        }

        // Coordinates are minted server-side: the deployment's private provider/bucket plus a tenant- and
        // workspace-scoped, unique object key. The client never supplies these (threats T5/T1).
        var objectKey = BuildObjectKey(organizationId, workspaceId);
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            _location.Provider,
            _location.Bucket,
            objectKey,
            normalizedContentType,
            now);

        // Mint the signed upload URL FIRST: a fail-closed storage error (AssetStorageNotConfiguredException)
        // then leaves no orphan pending asset behind, so the command has no side effect when storage is off.
        var uploadUrl = await _storage.CreateUploadUrlAsync(asset, cancellationToken).ConfigureAwait(false);

        await _assets.AddAsync(asset, cancellationToken).ConfigureAwait(false);

        return new AssetUploadIntent(asset, uploadUrl);
    }

    /// <summary>
    /// Mints a tenant- and workspace-scoped, collision-free storage object key for a new asset:
    /// <c>assets/{organizationId}/{workspaceId}/{uuid}</c>, where the per-object segment is a fresh
    /// time-ordered UUID. The path leads with the organization then the workspace, so a key always
    /// addresses an object inside its own tenant and workspace, and the fresh UUID makes two intents'
    /// objects distinct (threats T5/T1). The result contains no whitespace, so it is a valid object key
    /// (<see cref="Asset.IsValidObjectKey"/>).
    /// </summary>
    internal static string BuildObjectKey(Guid organizationId, Guid workspaceId)
        => $"assets/{organizationId}/{workspaceId}/{Guid.CreateVersion7()}";
}
