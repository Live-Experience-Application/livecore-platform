namespace LiveCore.Api.Assets;

/// <summary>
/// Thrown by the fail-closed default storage adapter (<see cref="UnconfiguredAssetStorage"/>) when an
/// asset storage operation is attempted but no concrete, S3-compatible object-storage adapter has been
/// configured (CORE-AST-002).
///
/// The concrete adapter and its object-storage endpoint/credentials are supplied by the deployment
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md; ADR 0006). When none is wired, Core must NOT serve asset bytes
/// some other (insecure) way: it FAILS CLOSED, so assets stay private by default (the epic acceptance
/// criterion; threat T4 "Asset leak" in docs/07_SECURITY_THREAT_MODEL.md). This mirrors how the host runs
/// without a database connection string or without an OIDC authority and denies cleanly rather than
/// degrading security. The consuming flows (CORE-AST-003 / CORE-AST-004) surface this as a fail-closed
/// response (a storage misconfiguration is an unavailable feature, never anonymous access).
///
/// The message carries only the attempted operation name — never any storage coordinate — so it is safe
/// for structured logs (threat T7 in docs/07_SECURITY_THREAT_MODEL.md).
/// </summary>
public sealed class AssetStorageNotConfiguredException : InvalidOperationException
{
    /// <summary>
    /// Creates a fail-closed storage error describing the operation that could not be served because no
    /// object-storage adapter is configured.
    /// </summary>
    public AssetStorageNotConfiguredException(AssetStorageOperation operation)
        : base($"No object storage is configured; cannot produce a signed {operation} URL.")
    {
        Operation = operation;
    }

    /// <summary>The operation that was attempted when storage was found to be unconfigured.</summary>
    public AssetStorageOperation Operation { get; }
}
