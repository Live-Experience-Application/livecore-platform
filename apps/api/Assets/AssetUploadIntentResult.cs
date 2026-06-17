// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

using LiveCore.Api.Entitlements;

namespace LiveCore.Api.Assets;

/// <summary>
/// Outcome of the upload-intent command (<see cref="AssetUploadIntentService.CreateAsync"/>, CORE-AST-003 +
/// the CORE-MON-006 storage-quota gate + the CORE-AST-007 allowlist/ceiling hardening). The command runs AFTER
/// the endpoint has authorized the caller; this result tells the endpoint how to respond without it
/// re-implementing the enforcement logic. A fail-closed storage error (no object storage configured) is NOT an
/// outcome here — it propagates as <see cref="AssetStorageNotConfiguredException"/> for the endpoint to map to
/// 503, exactly as before.
/// </summary>
public enum AssetUploadIntentOutcome
{
    /// <summary>
    /// The intent was created: the content type was allowed, the declared size was within the per-object
    /// ceiling, the workspace had storage headroom (or no storage quota governed it), the pending asset was
    /// registered and the signed upload URL was minted (the endpoint returns 201).
    /// </summary>
    Created = 1,

    /// <summary>
    /// The declared object size would take the workspace over its <c>asset.storage.bytes.max</c> storage
    /// quota, so the upload is rejected: nothing was consumed, no asset was persisted and no URL was minted
    /// (the endpoint returns 409). This is the fail-closed free-tier storage gate (CORE-MON-006).
    /// </summary>
    QuotaExceeded = 2,

    /// <summary>
    /// The declared content type is not on the deployment's configurable MIME allowlist (CORE-AST-007), so the
    /// upload is rejected FAIL-CLOSED before any quota is consumed and before any URL is minted (the endpoint
    /// returns 422). The rejection carries no storage coordinate (threat T4/T7).
    /// </summary>
    ContentTypeNotAllowed = 3,

    /// <summary>
    /// The declared object size exceeds the deployment's configurable absolute per-object size ceiling
    /// (CORE-AST-007), independent of the workspace storage quota, so the upload is rejected FAIL-CLOSED before
    /// any quota is consumed and before any URL is minted (the endpoint returns 413). The rejection names only
    /// the byte ceiling, never any storage coordinate (threat T4/T7).
    /// </summary>
    ObjectTooLarge = 4,
}

/// <summary>
/// The result of <see cref="AssetUploadIntentService.CreateAsync"/>: the <see cref="Outcome"/> and, depending
/// on it, the created <see cref="Intent"/> (on <see cref="AssetUploadIntentOutcome.Created"/>), the fail-closed
/// quota <see cref="QuotaDenial"/> (on <see cref="AssetUploadIntentOutcome.QuotaExceeded"/>) or the absolute
/// <see cref="SizeCeilingBytes"/> (on <see cref="AssetUploadIntentOutcome.ObjectTooLarge"/>). Each payload
/// carries only generic, client-safe facts (an entitlement key/limit, a byte ceiling), so the endpoint can
/// phrase its response without leaking internal state or any storage coordinate (threat T7).
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Intent">The registered intent when <see cref="Outcome"/> is <see cref="AssetUploadIntentOutcome.Created"/>; otherwise null.</param>
/// <param name="QuotaDenial">The quota decision when <see cref="Outcome"/> is <see cref="AssetUploadIntentOutcome.QuotaExceeded"/>; otherwise null.</param>
/// <param name="SizeCeilingBytes">The absolute per-object byte ceiling when <see cref="Outcome"/> is <see cref="AssetUploadIntentOutcome.ObjectTooLarge"/>; otherwise null.</param>
internal readonly record struct AssetUploadIntentResult(
    AssetUploadIntentOutcome Outcome,
    AssetUploadIntent? Intent,
    QuotaEnforcementDecision? QuotaDenial,
    long? SizeCeilingBytes)
{
    /// <summary>A successful upload-intent registration.</summary>
    public static AssetUploadIntentResult Created(AssetUploadIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return new AssetUploadIntentResult(AssetUploadIntentOutcome.Created, intent, QuotaDenial: null, SizeCeilingBytes: null);
    }

    /// <summary>The upload would exceed the workspace's storage quota; nothing was consumed or persisted.</summary>
    public static AssetUploadIntentResult QuotaExceeded(QuotaEnforcementDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new AssetUploadIntentResult(AssetUploadIntentOutcome.QuotaExceeded, Intent: null, decision, SizeCeilingBytes: null);
    }

    /// <summary>
    /// The declared content type is not on the configurable MIME allowlist (CORE-AST-007); nothing was consumed
    /// or persisted and no URL was minted.
    /// </summary>
    public static AssetUploadIntentResult ContentTypeNotAllowed()
        => new(AssetUploadIntentOutcome.ContentTypeNotAllowed, Intent: null, QuotaDenial: null, SizeCeilingBytes: null);

    /// <summary>
    /// The declared object size exceeds the absolute per-object ceiling (CORE-AST-007); nothing was consumed or
    /// persisted and no URL was minted. The <paramref name="sizeCeilingBytes"/> is the generic, client-safe
    /// ceiling the endpoint phrases the 413 with.
    /// </summary>
    public static AssetUploadIntentResult ObjectTooLarge(long sizeCeilingBytes)
        => new(AssetUploadIntentOutcome.ObjectTooLarge, Intent: null, QuotaDenial: null, sizeCeilingBytes);
}
