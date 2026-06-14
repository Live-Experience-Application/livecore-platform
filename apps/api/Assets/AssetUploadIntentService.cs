using LiveCore.Api.Entitlements;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Assets;

/// <summary>
/// The upload-intent command of the Assets module (CORE-AST-003) — the "Create upload intent" step of the
/// asset lifecycle (docs/12_STORAGE_ASSETS.md). It registers a new <see cref="AssetStatus.Pending"/>
/// <see cref="Asset"/>, ATOMICALLY consumes the declared object size against the workspace's storage quota
/// (CORE-MON-006) and mints the short-lived, signed URL the authorized client uploads the object with. It is
/// a plain command service over <see cref="IAssetRepository"/>, the <see cref="IAssetStorage"/> adapter port
/// (CORE-AST-002), the deployment's <see cref="AssetStorageLocation"/>, the reused
/// <see cref="QuotaEnforcementService"/> and the shared <see cref="TransactionalUnitOfWork"/>, taking
/// explicit already-resolved inputs, exactly like <see cref="LiveCore.Api.Visibility.RevealService"/>: the
/// calling endpoint resolves the tenant, the workspace and authorizes the caller's role BEFORE invoking it.
/// This service performs no authorization itself.
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
/// SERVER-SIDE STORAGE QUOTA (CORE-MON-006). The client-DECLARED object size is atomically check-and-consumed
/// against the workspace's <c>asset.storage.bytes.max</c> quota for the
/// <see cref="EntitlementSubjectType.Workspace"/> subject through <see cref="QuotaEnforcementService.TryConsumeAsync"/>
/// (CORE-CONC-004): a single limit-guarded statement, so concurrent uploads can never both overrun the cap.
/// When the workspace would exceed its granted storage limit the intent is REJECTED
/// (<see cref="AssetUploadIntentResult.QuotaExceeded"/>) — nothing is consumed, no asset is persisted and no
/// URL is minted — so the documented free-tier storage cap cannot be bypassed by a client that does not render
/// the paywall (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md: "Free limits cannot be bypassed by clients").
/// Core enforces only a quota that EXISTS: when no <c>asset.storage.bytes.max</c> quota governs the deployment
/// the upload is ungoverned and proceeds; a paid plan's larger or unlimited (fair-use) grant admits more. The
/// consumed bytes are recorded on the asset (<see cref="Asset.SizeBytes"/>) so the host-initiated deletion
/// (<see cref="AssetDeletionService"/>) releases exactly them, and the recorded usage reflects the workspace's
/// CURRENT stored bytes rather than a lifetime total.
///
/// PRIVATE BY DEFAULT, FAIL-CLOSED, ATOMIC. The asset is created <see cref="AssetStatus.Pending"/> with no
/// public affordance (the epic acceptance criterion: "Assets are private by default and accessed only through
/// authorized signed URLs"; threat T4 "Asset leak"). The consume, the URL mint and the row persist run inside
/// ONE <see cref="TransactionalUnitOfWork"/>: when storage is not configured the fail-closed
/// <see cref="UnconfiguredAssetStorage"/> throws <see cref="AssetStorageNotConfiguredException"/> INSIDE the
/// transaction, which rolls back having consumed nothing and persisted nothing — no orphan pending asset and
/// no leaked quota (the same fail-closed posture as the deletion command, which also calls storage inside its
/// transaction). The signed URL is itself a secret (it embeds the object key and signature); it is returned to
/// the authorized caller only and never logged (threats T4/T7; <see cref="SignedAssetUrl.ToString"/> and
/// <see cref="Asset.ToString"/> both exclude it / the coordinates).
/// </summary>
internal sealed class AssetUploadIntentService
{
    private readonly IAssetRepository _assets;
    private readonly IAssetStorage _storage;
    private readonly AssetStorageLocation _location;
    private readonly QuotaEnforcementService _quotaEnforcement;
    private readonly TransactionalUnitOfWork _unitOfWork;

    public AssetUploadIntentService(
        IAssetRepository assets,
        IAssetStorage storage,
        AssetStorageLocation location,
        QuotaEnforcementService quotaEnforcement,
        TransactionalUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(quotaEnforcement);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        _assets = assets;
        _storage = storage;
        _location = location;
        _quotaEnforcement = quotaEnforcement;
        _unitOfWork = unitOfWork;
    }

    /// <summary>
    /// Registers a new pending asset in the given workspace (owned by the given organization), created by
    /// the given authenticated user, with the client-declared content type, atomically consuming the declared
    /// <paramref name="sizeBytes"/> against the workspace's storage quota and minting the short-lived signed
    /// upload URL. The storage coordinates are assigned server-side. Returns <see cref="AssetUploadIntentResult.Created"/>
    /// (the registered asset and the signed URL) when the workspace has storage headroom, or
    /// <see cref="AssetUploadIntentResult.QuotaExceeded"/> when the declared size would take the workspace over
    /// its <c>asset.storage.bytes.max</c> limit (nothing is consumed or persisted).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the asset belongs to (the storage-quota subject).</param>
    /// <param name="createdByUserProfileId">The authenticated user registering the asset (the audited creator).</param>
    /// <param name="contentType">The client-declared MIME content type of the object to be uploaded.</param>
    /// <param name="sizeBytes">The client-declared object size in bytes (strictly positive); reserved against the storage quota.</param>
    /// <param name="now">The command timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id or creator id is empty, or the content type is not a valid
    /// content-type token.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The declared size is not strictly positive.</exception>
    /// <exception cref="AssetStorageNotConfiguredException">
    /// No object storage is configured; nothing is consumed or persisted (fail-closed).
    /// </exception>
    public async Task<AssetUploadIntentResult> CreateAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid createdByUserProfileId,
        string contentType,
        long sizeBytes,
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

        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "A declared size must be strictly positive.");
        }

        // Coordinates are minted server-side: the deployment's private provider/bucket plus a tenant- and
        // workspace-scoped, unique object key. The client never supplies these (threats T5/T1). The declared
        // size is recorded on the asset so the deletion command can release exactly the reserved bytes.
        var objectKey = BuildObjectKey(organizationId, workspaceId);
        var asset = Asset.Create(
            organizationId,
            workspaceId,
            createdByUserProfileId,
            _location.Provider,
            _location.Bucket,
            objectKey,
            normalizedContentType,
            now,
            sizeBytes);

        // ONE unit of work (CORE-CONC-002): the storage-quota consume, the signed-URL mint and the asset-row
        // persist commit together or roll back together. Every repository and the quota service write through
        // the same scoped DbContext the unit of work begins the transaction on. The transaction is opened
        // inside the EF execution strategy's ExecuteAsync, so it stays correct under the CORE-CONC-003 retrying
        // strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // 1) Atomic check-and-consume the declared bytes against the workspace's storage quota
                //    (CORE-CONC-004). The check and the increment are a SINGLE limit-guarded statement, so two
                //    concurrent uploads racing for the last bytes can never both pass. A denial consumes
                //    NOTHING, so an over-quota upload reserves no storage and (returning here) commits a no-op.
                //    When no asset.storage.bytes.max quota governs the deployment the consume is a no-op and the
                //    upload proceeds; an unlimited (fair-use) grant always has headroom.
                var quotaDecision = await _quotaEnforcement
                    .TryConsumeAsync(
                        EntitlementSubjectType.Workspace,
                        workspaceId,
                        QuotaEntitlementKeys.AssetStorageBytesMax,
                        sizeBytes,
                        transactionCancellationToken)
                    .ConfigureAwait(false);
                if (!quotaDecision.IsAllowed)
                {
                    // Over the workspace's storage limit: reject. Nothing was consumed, and no URL is minted and
                    // no row persisted, so the upload has no side effect (the storage analogue of an over-cap
                    // participant join, fail-closed).
                    return AssetUploadIntentResult.QuotaExceeded(quotaDecision);
                }

                // 2) Mint the signed upload URL. A fail-closed storage error (AssetStorageNotConfiguredException)
                //    thrown here propagates out of the transaction, which rolls back the consume above — so an
                //    unconfigured storage backend leaves no orphan pending asset AND no leaked quota (threat T4).
                var uploadUrl = await _storage
                    .CreateUploadUrlAsync(asset, transactionCancellationToken)
                    .ConfigureAwait(false);

                // 3) Persist the pending asset metadata row (carrying the reserved size).
                await _assets.AddAsync(asset, transactionCancellationToken).ConfigureAwait(false);

                return AssetUploadIntentResult.Created(new AssetUploadIntent(asset, uploadUrl));
            },
            cancellationToken).ConfigureAwait(false);
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
