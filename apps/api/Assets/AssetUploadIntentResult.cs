// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the upload-intent command (<see cref="AssetUploadIntentService.CreateAsync"/>, CORE-AST-003 +
/// the CORE-MON-006 storage-quota gate). The command runs AFTER the endpoint has authorized the caller; this
/// result tells the endpoint how to respond without it re-implementing the enforcement logic. A fail-closed
/// storage error (no object storage configured) is NOT an outcome here — it propagates as
/// <see cref="AssetStorageNotConfiguredException"/> for the endpoint to map to 503, exactly as before.
/// </summary>
public enum AssetUploadIntentOutcome
{
    /// <summary>
    /// The intent was created: the workspace had storage headroom (or no storage quota governed it), the
    /// pending asset was registered and the signed upload URL was minted (the endpoint returns 201).
    /// </summary>
    Created = 1,

    /// <summary>
    /// The declared object size would take the workspace over its <c>asset.storage.bytes.max</c> storage
    /// quota, so the upload is rejected: nothing was consumed, no asset was persisted and no URL was minted
    /// (the endpoint returns 409). This is the fail-closed free-tier storage gate (CORE-MON-006).
    /// </summary>
    QuotaExceeded = 2,
}

/// <summary>
/// The result of <see cref="AssetUploadIntentService.CreateAsync"/>: the <see cref="Outcome"/> and, depending
/// on it, either the created <see cref="Intent"/> (on <see cref="AssetUploadIntentOutcome.Created"/>) or the
/// fail-closed quota <see cref="QuotaDenial"/> (on <see cref="AssetUploadIntentOutcome.QuotaExceeded"/>). The
/// quota denial carries only the generic, client-safe quota facts (the entitlement key and limit), so the
/// endpoint can phrase a 409 without leaking internal state (threat T7).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Intent">The registered intent when <see cref="Outcome"/> is <see cref="AssetUploadIntentOutcome.Created"/>; otherwise null.</param>
/// <param name="QuotaDenial">The quota decision when <see cref="Outcome"/> is <see cref="AssetUploadIntentOutcome.QuotaExceeded"/>; otherwise null.</param>
internal readonly record struct AssetUploadIntentResult(
    AssetUploadIntentOutcome Outcome,
    AssetUploadIntent? Intent,
    QuotaEnforcementDecision? QuotaDenial)
{
    /// <summary>A successful upload-intent registration.</summary>
    public static AssetUploadIntentResult Created(AssetUploadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new AssetUploadIntentResult(AssetUploadIntentOutcome.Created, intent, QuotaDenial: null);
    }

    /// <summary>The upload would exceed the workspace's storage quota; nothing was consumed or persisted.</summary>
    public static AssetUploadIntentResult QuotaExceeded(QuotaEnforcementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new AssetUploadIntentResult(AssetUploadIntentOutcome.QuotaExceeded, Intent: null, decision);
    }
}
