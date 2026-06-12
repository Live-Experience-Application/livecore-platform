namespace LiveCore.Api.Assets;

/// <summary>
/// The S3-compatible storage adapter PORT (CORE-AST-002) — the single seam between the Core platform and
/// the object storage that holds an asset's binary content. The Assets module owns the "storage adapter"
/// and "signed URL creation" (docs/05_MODULE_CONTRACTS.md); this interface is that adapter's contract.
/// Core uses an S3-compatible abstraction so the same code path works against RustFS (self-hosted) or any
/// S3-compatible provider (hosted) (docs/02_ARCHITECTURE.md "S3-compatible storage abstraction";
/// docs/12_STORAGE_ASSETS.md; ADR 0006).
///
/// WHY A PORT (and not a concrete provider here): the binary content never lives in PostgreSQL — it lives
/// in private object storage (docs/12_STORAGE_ASSETS.md; "Avoid storing large binary files in
/// PostgreSQL") — and the concrete adapter needs the deployment's object-storage endpoint and credentials
/// (docs/13_SELF_HOSTING_REQUIREMENTS.md lists "object storage endpoint and credentials" as runtime
/// configuration). So the provider-specific implementation (and its SDK) is supplied by the deployment
/// (livecore-deploy), exactly as the realtime scale-out backplane defines a port here and a
/// Valkey/Redis-backed implementation is supplied by deployment (CORE-RT-006, <c>IRealtimeBackplane</c>).
/// This keeps Core free of an object-storage SDK dependency and free of any storage credentials in source
/// (threat T7). Until a concrete adapter is wired, the default registration is the fail-closed
/// <see cref="UnconfiguredAssetStorage"/>, so asset access is DENIED rather than served insecurely — the
/// private-by-default posture holds even when storage is not configured.
///
/// SECURITY CONTRACT — every implementation MUST honor it (the epic acceptance criterion: "Assets are
/// private by default and accessed only through authorized signed URLs"; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md):
/// <list type="bullet">
///   <item>
///   Buckets are PRIVATE by default and object listing is never exposed (docs/12_STORAGE_ASSETS.md "no
///   public object listing"). The adapter never produces a public or static URL; the only access it ever
///   yields is a <see cref="SignedAssetUrl"/>, which by its type is absolute, signed and short-lived.
///   </item>
///   <item>
///   Both operations are SHORT-LIVED signed URLs (docs/12_STORAGE_ASSETS.md "signed URLs are
///   short-lived"; "Avoid long-lived signed URLs"). The <see cref="SignedAssetUrl"/> type enforces the
///   maximum lifetime, so an adapter cannot mint a long-lived or non-expiring URL.
///   </item>
///   <item>
///   The adapter signs a URL only for an EXISTING, already-resolved <see cref="Asset"/>'s own storage
///   coordinates — never for an arbitrary, caller-supplied bucket or object key. The asset is loaded
///   through the tenant- and workspace-scoped <see cref="IAssetRepository"/> before it reaches the
///   adapter, so a signed URL can only ever be minted for an object inside the caller's tenant and
///   workspace (threats T5/T1).
///   </item>
/// </list>
///
/// AUTHORIZATION IS UPSTREAM, NOT HERE. This port does not decide WHETHER the caller may upload or
/// download — that is the calling flow's responsibility: the upload-intent endpoint (CORE-AST-003) and
/// the signed-download endpoint (CORE-AST-004) authorize the caller server-side (role + tenant +
/// workspace + visibility) and only THEN ask the adapter to mint a URL ("upload intent requires
/// authorization"; "download URL requires authorization", docs/12_STORAGE_ASSETS.md;
/// docs/06_AUTHORIZATION_MATRIX.md). Minting a signed URL is the last step after the permission check has
/// passed; the adapter is a dumb, secure signer. The endpoints, their role/tenant authorization and their
/// negative-authorization (foreign-tenant / wrong-role) tests land with those flow stories.
///
/// CLEANUP (CORE-AST-006). The port also exposes one SERVER-SIDE operation — <see cref="DeleteObjectAsync"/>
/// — that hands no URL to any client: it removes an asset's object from its private bucket. The background
/// asset cleanup job uses it to reclaim the orphaned objects of abandoned pending upload intents (an upload
/// intent that was registered but never confirmed). Deletion only ever REMOVES access; it never widens it,
/// so it cannot weaken the private-by-default posture. It is fail-closed exactly like the signing
/// operations: when no concrete adapter is configured it throws <see cref="AssetStorageNotConfiguredException"/>
/// rather than silently pretending success, so the cleanup job never deletes a metadata row whose object it
/// could not delete and so never orphans an object.
/// </summary>
public interface IAssetStorage
{
    /// <summary>
    /// Mints a short-lived, signed URL that lets a client upload the bytes of the given asset's object
    /// directly into its private bucket (the "client uploads to storage" step of
    /// docs/12_STORAGE_ASSETS.md's asset lifecycle). The asset is the already-created, tenant- and
    /// workspace-scoped metadata row (typically still <see cref="AssetStatus.Pending"/>); the adapter
    /// signs for its own storage coordinates only. The caller (the upload-intent flow, CORE-AST-003) must
    /// have authorized the request before calling this.
    /// </summary>
    /// <param name="asset">The asset whose object the upload URL targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A short-lived, signed upload URL.</returns>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    /// <exception cref="AssetStorageNotConfiguredException">No object storage is configured (fail closed).</exception>
    Task<SignedAssetUrl> CreateUploadUrlAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>
    /// Mints a short-lived, signed URL that lets a client download the bytes of the given asset's object
    /// from its private bucket ("download URL requires authorization", docs/12_STORAGE_ASSETS.md). The
    /// asset is the already-loaded, tenant- and workspace-scoped metadata row; the adapter signs for its
    /// own storage coordinates only. The caller (the signed-download flow, CORE-AST-004) must have
    /// authorized the request — including the resource's visibility — before calling this; this method
    /// performs no authorization itself.
    /// </summary>
    /// <param name="asset">The asset whose object the download URL targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A short-lived, signed download URL.</returns>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    /// <exception cref="AssetStorageNotConfiguredException">No object storage is configured (fail closed).</exception>
    Task<SignedAssetUrl> CreateDownloadUrlAsync(Asset asset, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the given asset's object from its private bucket (the cleanup step of the asset lifecycle,
    /// CORE-AST-006). The adapter deletes the object directly with the deployment's own storage credentials —
    /// no signed URL is produced and no bytes are served, so this can only ever REMOVE access (threat T4
    /// "Asset leak"). The asset is the already-loaded, tenant- and workspace-scoped metadata row; the adapter
    /// addresses its own storage coordinates only, never an arbitrary bucket or object key (threats T5/T1).
    /// The caller is the background asset cleanup job, which has already determined the asset is an expired,
    /// unconfirmed (pending) upload intent.
    ///
    /// Deletion is IDEMPOTENT: removing an object that does not exist (an upload intent whose client never
    /// actually uploaded anything) completes successfully rather than failing, so the cleanup job can safely
    /// reclaim a pending asset whether or not its object was ever written.
    /// </summary>
    /// <param name="asset">The asset whose object is removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">The asset is null.</exception>
    /// <exception cref="AssetStorageNotConfiguredException">No object storage is configured (fail closed).</exception>
    Task DeleteObjectAsync(Asset asset, CancellationToken cancellationToken);
}
