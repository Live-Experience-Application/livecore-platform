// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the asset confirm-upload command (<see cref="AssetConfirmUploadService.ConfirmAsync"/>,
/// CORE-ALC-001). The command runs AFTER the endpoint has resolved the tenant, discovered the asset's
/// workspace and authorized the caller's role; this result tells the endpoint how to respond without it
/// re-implementing the lifecycle logic.
/// </summary>
public enum AssetConfirmUploadOutcome
{
    /// <summary>
    /// The asset was confirmed: it was still <see cref="AssetStatus.Pending"/>, so the guarded
    /// <see cref="Asset.MarkAvailable"/> transition recorded its size and checksum, moved it to
    /// <see cref="AssetStatus.Available"/> and appended the audit fact (the endpoint returns 200 with the
    /// confirmed asset).
    /// </summary>
    Confirmed = 1,

    /// <summary>
    /// No asset with that id exists in the resolved tenant and workspace (an unknown id, or one belonging to
    /// another workspace/tenant). Nothing was changed; the endpoint hides it as a 404 (threats T1/T5).
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// The asset exists but is NOT <see cref="AssetStatus.Pending"/> (it was already confirmed, so it is
    /// <see cref="AssetStatus.Available"/>): confirming it again is an out-of-state command. Nothing was
    /// changed and no audit fact was appended; the endpoint returns 409. This is the fail-closed
    /// "Pending-to-Available is the only transition" guard — a confirm can never silently overwrite a
    /// different already-recorded size/checksum.
    /// </summary>
    NotPending = 3,
}

/// <summary>
/// The result of <see cref="AssetConfirmUploadService.ConfirmAsync"/>: the <see cref="Outcome"/> and, on
/// <see cref="AssetConfirmUploadOutcome.Confirmed"/>, the now-<see cref="AssetStatus.Available"/>
/// <see cref="Asset"/> the endpoint projects its response from. The other outcomes carry no asset (the
/// endpoint maps them to a hidden 404 / a 409 with a generic body), so no asset state ever leaks to a caller
/// the command did not serve (threat T7).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Asset">The confirmed asset when <see cref="Outcome"/> is <see cref="AssetConfirmUploadOutcome.Confirmed"/>; otherwise null.</param>
internal readonly record struct AssetConfirmUploadResult(AssetConfirmUploadOutcome Outcome, Asset? Asset)
{
    /// <summary>A successful confirmation; carries the now-available asset.</summary>
    public static AssetConfirmUploadResult Confirmed(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new AssetConfirmUploadResult(AssetConfirmUploadOutcome.Confirmed, asset);
    }

    /// <summary>No such asset in the resolved tenant and workspace; nothing changed.</summary>
    public static AssetConfirmUploadResult NotFound()
        => new(AssetConfirmUploadOutcome.NotFound, Asset: null);

    /// <summary>The asset is not pending (already confirmed); nothing changed.</summary>
    public static AssetConfirmUploadResult NotPending()
        => new(AssetConfirmUploadOutcome.NotPending, Asset: null);
}
