// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Audit;
using LiveCore.Api.Persistence;

namespace LiveCore.Api.Assets;

/// <summary>
/// The asset confirm-upload command of the Assets module (CORE-ALC-001, the "Asset Lifecycle and Attachment
/// Completeness" epic). A host confirms a successful upload; this service drives the existing
/// <see cref="Asset.MarkAvailable"/> domain transition so a still-<see cref="AssetStatus.Pending"/> asset
/// records its uploaded size and checksum, moves to <see cref="AssetStatus.Available"/> (and therefore
/// becomes downloadable, CORE-AST-004), and the transition is audited — the "client confirms upload -&gt; Core
/// stores asset metadata" step of the asset lifecycle (docs/12_STORAGE_ASSETS.md).
///
/// THE GAP THIS CLOSES. The <see cref="Asset.MarkAvailable"/> aggregate method and
/// <see cref="IAssetRepository.UpdateAsync"/> already existed but were wired to NO transport, so an asset
/// could never leave <see cref="AssetStatus.Pending"/> except via the background cleanup that reclaims an
/// abandoned, never-confirmed intent (CORE-AST-006). This is the explicit confirm route that completes the
/// upload — it is the ONLY <see cref="AssetStatus.Pending"/>-to-<see cref="AssetStatus.Available"/>
/// transition (a storage-event/webhook-driven transition is a documented alternative, out of scope here).
///
/// NO STORAGE DEPENDENCY. Unlike the upload-intent and deletion commands, confirming touches NO object
/// storage: the upload itself already happened against the signed PUT URL (whose SigV4 signature binds the
/// declared content type and length, CORE-AST-008), so confirmation only stamps the now-known metadata and
/// records the lifecycle fact. The explicit confirm route therefore needs no new inbound infrastructure.
///
/// FAIL-CLOSED, ATOMIC, AUDITED. The service takes the already-resolved tenant and workspace and an asset id,
/// and loads the asset through the tenant- AND workspace-scoped
/// <see cref="IAssetRepository.FindByIdAsync(Guid, Guid, Guid, CancellationToken)"/> FIRST: an asset in
/// another workspace, or in a workspace owned by another tenant, is never found and so never confirmed, even
/// when its surrogate id is known (threats T1/T5). Confirming a non-pending asset is rejected
/// (<see cref="AssetConfirmUploadOutcome.NotPending"/>) BEFORE any transaction is opened — the guarded
/// transition is the only path to <see cref="AssetStatus.Available"/>, so a confirm can never silently
/// overwrite a different already-recorded size/checksum. The transition and the audit append run inside ONE
/// <see cref="TransactionalUnitOfWork"/> (CORE-CONC-002), exactly like the deletion command: the asset update
/// and the append-only <see cref="AuditAction.AssetConfirmed"/> record commit together or roll back together,
/// so a confirmation is recorded as a fact or it does not happen. The endpoint
/// (<see cref="AssetEndpoints"/>) performs the authentication, tenant resolution and role authorization
/// before calling in; this service is the authorized command's effect and performs no authorization itself
/// (exactly like <see cref="AssetUploadIntentService"/> and <see cref="AssetDeletionService"/>).
/// </summary>
internal sealed class AssetConfirmUploadService
{
    private readonly TransactionalUnitOfWork _unitOfWork;
    private readonly IAssetRepository _assets;
    private readonly IAuditLogRepository _audit;

    public AssetConfirmUploadService(
        TransactionalUnitOfWork unitOfWork,
        IAssetRepository assets,
        IAuditLogRepository audit)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(audit);
        _unitOfWork = unitOfWork;
        _assets = assets;
        _audit = audit;
    }

    /// <summary>
    /// Confirms the upload of the asset identified by <paramref name="assetId"/> within the given tenant and
    /// workspace, recording its uploaded <paramref name="sizeBytes"/> and <paramref name="checksum"/>, moving
    /// it <see cref="AssetStatus.Pending"/> -&gt; <see cref="AssetStatus.Available"/> and appending an
    /// append-only <see cref="AuditAction.AssetConfirmed"/> record — all atomically. Returns
    /// <see cref="AssetConfirmUploadOutcome.Confirmed"/> (with the now-available asset) when the asset existed
    /// and was pending, <see cref="AssetConfirmUploadOutcome.NotPending"/> when it exists but is already
    /// confirmed (nothing changed), or <see cref="AssetConfirmUploadOutcome.NotFound"/> when no asset with that
    /// id exists in the resolved tenant and workspace (nothing changed).
    /// </summary>
    /// <param name="organizationId">The tenant that owns the workspace (checked before the workspace).</param>
    /// <param name="workspaceId">The workspace the asset belongs to.</param>
    /// <param name="assetId">The surrogate id of the asset to confirm.</param>
    /// <param name="actorUserProfileId">
    /// The authenticated host who confirmed the upload — the audited actor. Supplied by the endpoint from the
    /// resolved tenant context. Must be non-empty.
    /// </param>
    /// <param name="sizeBytes">The confirmed uploaded object size in bytes (non-negative).</param>
    /// <param name="checksum">The confirmed uploaded object checksum (a non-blank, bounded token).</param>
    /// <param name="now">The command timestamp (the audit record's time and the asset's update time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// The organization id, workspace id, asset id or actor id is empty, or the checksum violates an invariant.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The size is negative.</exception>
    public async Task<AssetConfirmUploadResult> ConfirmAsync(
        Guid organizationId,
        Guid workspaceId,
        Guid assetId,
        Guid actorUserProfileId,
        long sizeBytes,
        string checksum,
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

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(assetId));
        }

        if (actorUserProfileId == Guid.Empty)
        {
            throw new ArgumentException("Actor user profile id must not be empty.", nameof(actorUserProfileId));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Size in bytes must not be negative.");
        }

        var normalizedChecksum = checksum?.Trim() ?? string.Empty;
        if (!Asset.IsValidChecksum(normalizedChecksum))
        {
            throw new ArgumentException("Checksum violates the checksum invariants.", nameof(checksum));
        }

        // Load the asset WITHIN the resolved tenant AND workspace. FindByIdAsync leads with organization_id
        // then workspace_id then the asset id, so an asset in another workspace or tenant is never returned
        // even when the surrogate id matches; an unknown id is simply null. Confirming a non-existent asset
        // reveals nothing and changes nothing — a safe NotFound, and no transaction is opened for it.
        var asset = await _assets
            .FindByIdAsync(organizationId, workspaceId, assetId, cancellationToken)
            .ConfigureAwait(false);
        if (asset is null)
        {
            return AssetConfirmUploadResult.NotFound();
        }

        // Only a still-pending asset may be confirmed (the guarded Pending -> Available transition is the only
        // path to Available). A non-pending asset is rejected BEFORE any transaction is opened, so a re-confirm
        // is a clean out-of-state 409 that changes nothing and never overwrites a different recorded
        // size/checksum (the story's "a non-Pending asset is 409", fail-closed).
        if (!asset.CanMarkAvailable)
        {
            return AssetConfirmUploadResult.NotPending();
        }

        // ONE unit of work (CORE-CONC-002): the asset update and the audit append commit together or roll back
        // together, so a confirmation is applied whole or not at all. Both injected dependencies write through
        // the same scoped DbContext TransactionalUnitOfWork begins the transaction on. The transaction is
        // opened inside the EF execution strategy's ExecuteAsync, so it stays correct under the CORE-CONC-003
        // retrying strategy.
        return await _unitOfWork.ExecuteAsync(
            async transactionCancellationToken =>
            {
                // The before status (always Pending here — guarded above) is captured for the audit transition
                // record before MarkAvailable flips it.
                var previousState = asset.Status.ToString();

                // 1) Drive the existing domain transition: record the uploaded size/checksum and move
                //    Pending -> Available. MarkAvailable re-asserts the guard, so a concurrent confirm that
                //    slipped past the pre-check would throw rather than double-confirm.
                asset.MarkAvailable(sizeBytes, normalizedChecksum, now);
                await _assets.UpdateAsync(asset, transactionCancellationToken).ConfigureAwait(false);

                // 2) AUDIT (the story's "the transition is audited"): a confirmation is a real lifecycle state
                //    transition, so append an append-only record capturing the actor (the host who confirmed),
                //    the confirmed asset and the before/after status names. The uploaded checksum and the
                //    recorded size are never written to the audit log — only identifiers and the generic state
                //    names (threats T4/T7).
                var entry = AuditLogEntry.ForAssetConfirmation(
                    organizationId,
                    workspaceId,
                    actorUserProfileId,
                    nameof(Asset),
                    assetId,
                    previousState,
                    asset.Status.ToString(),
                    now);
                await _audit.AppendAsync(entry, transactionCancellationToken).ConfigureAwait(false);

                return AssetConfirmUploadResult.Confirmed(asset);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
