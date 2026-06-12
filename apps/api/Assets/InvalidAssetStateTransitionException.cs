namespace LiveCore.Api.Assets;

/// <summary>
/// Thrown when an <see cref="Asset"/> lifecycle transition (<see cref="Asset.MarkAvailable"/>) is
/// attempted from a state in which it is not legal (CORE-AST-001). The asset state machine is
/// <see cref="AssetStatus.Pending"/> -&gt; <see cref="AssetStatus.Available"/>, and the confirm
/// transition is valid only from <see cref="AssetStatus.Pending"/>; every other attempt is a genuine
/// state error, not a no-op (docs/12_STORAGE_ASSETS.md: "client confirms upload -&gt; Core stores asset
/// metadata" presupposes a still-pending asset).
///
/// The throw is deliberate and chosen over a silent no-op: confirming an already-available asset would
/// otherwise silently overwrite a different already-recorded size/checksum, so an out-of-order confirm
/// surfaces as an error instead of corrupting the recorded upload metadata. So the application layer can
/// branch WITHOUT catching, the aggregate also exposes the <see cref="Asset.CanMarkAvailable"/>
/// predicate; this exception is the fail-closed guard for callers that do not pre-check. Command-level
/// retry idempotency (an at-most-once confirm command) is a later upload-flow concern (CORE-AST-003, the
/// <c>idempotency_keys</c> table), not an aggregate no-op.
///
/// The message carries only the aggregate's id and the from/to status names (never the storage
/// coordinates), so it is safe for structured logs (threats T4 and T7 in
/// docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class InvalidAssetStateTransitionException : InvalidOperationException
{
    /// <summary>
    /// Creates a transition error describing the asset, the status it is in and the status the rejected
    /// transition targeted.
    /// </summary>
    public InvalidAssetStateTransitionException(Guid assetId, AssetStatus from, AssetStatus to)
        : base($"Asset {assetId} cannot transition from {from} to {to}.")
    {
        AssetId = assetId;
        From = from;
        To = to;
    }

    /// <summary>The id of the asset whose transition was rejected.</summary>
    public Guid AssetId { get; }

    /// <summary>The status the asset was in when the transition was attempted.</summary>
    public AssetStatus From { get; }

    /// <summary>The status the rejected transition targeted.</summary>
    public AssetStatus To { get; }
}
