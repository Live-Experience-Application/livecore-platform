// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// The result of an upload-intent command (CORE-AST-003): the newly registered, <see cref="AssetStatus.Pending"/>
/// <see cref="Asset"/> and the short-lived, signed <see cref="UploadUrl"/> the authorized client uses to
/// upload the object directly to its private bucket (the "Create upload intent -&gt; client uploads to
/// storage" step of docs/12_STORAGE_ASSETS.md). This is the Assets module's internal command result; the
/// endpoint projects it into the participant-safe response DTO. The <see cref="UploadUrl"/> is a secret
/// (it embeds the object key and signature) and is returned to the authorized caller only — it is never
/// logged (threats T4/T7 in docs/07_SECURITY_THREAT_MODEL.md; see <see cref="ToString"/>).
/// </summary>
/// <param name="Asset">The registered pending asset metadata row.</param>
/// <param name="UploadUrl">The short-lived, signed upload URL for the asset's own object.</param>
internal sealed record AssetUploadIntent(Asset Asset, SignedAssetUrl UploadUrl)
{
    /// <summary>
    /// Log-safe representation: the asset (its own <see cref="Asset.ToString"/> excludes the storage
    /// coordinates) and the signed URL's log-safe form (which excludes the secret URL). The auto-generated
    /// record <c>ToString</c> would otherwise print the full <see cref="SignedAssetUrl.Url"/>, so it is
    /// overridden to keep a leaked log line from being replayable as asset access (threats T4/T7).
    /// </summary>
    public override string ToString() => $"AssetUploadIntent asset=[{Asset}] upload=[{UploadUrl}]";
}
