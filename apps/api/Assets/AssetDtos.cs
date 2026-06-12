namespace LiveCore.Api.Assets;

/// <summary>
/// Request body for the upload-intent command (CORE-AST-003,
/// <c>POST /api/v1/assets/upload-intent</c>, csv/api_routes.csv "Creates upload intent", roles
/// Host/CoHost/Owner/Admin). The route has no path parameters, so the target tenant and workspace are
/// supplied in the body: <see cref="OrganizationSlug"/> resolves the tenant (token organization claim AND
/// persisted membership — defence in depth, threat T5) and <see cref="WorkspaceId"/> names the workspace
/// the asset belongs to. The client declares only the <see cref="ContentType"/> of the object it intends
/// to upload; the storage coordinates (provider, bucket, object key) are minted SERVER-SIDE, never
/// accepted from the client, so an upload can never be pointed at an arbitrary bucket or another tenant's
/// object (threats T5/T1; docs/12_STORAGE_ASSETS.md). The body carries no vertical vocabulary
/// (docs/04_PRODUCT_BOUNDARIES.md).
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the workspace, used to resolve the tenant context.
/// </param>
/// <param name="WorkspaceId">The workspace the new asset belongs to.</param>
/// <param name="ContentType">
/// The MIME content type of the object that will be uploaded (for example <c>image/png</c>). Validated as
/// a well-formed content-type token; an invalid or oversize value is a 400.
/// </param>
public sealed record CreateUploadIntentRequest(
    string? OrganizationSlug,
    Guid WorkspaceId,
    string? ContentType);

/// <summary>
/// Response body of the upload-intent command (CORE-AST-003). It returns the registered asset's id and
/// pending status plus the short-lived, signed upload URL (and its expiry) the client uploads the object
/// with. The asset is PRIVATE: the only access this hands out is the single short-lived signed upload URL
/// (the epic acceptance criterion; threat T4 "Asset leak"). The response carries no internal storage
/// coordinates beyond what the signed URL itself embeds, and no authorization rationale
/// (docs/08_API_CONTRACTS.md; threat T7).
///
/// The <see cref="UploadUrl"/> is a secret (it embeds the object key and signature); it is delivered to
/// the authorized caller over HTTPS and must never be logged. The auto-generated record <c>ToString</c> is
/// overridden to exclude it (threats T4/T7).
/// </summary>
/// <param name="AssetId">The surrogate id of the registered pending asset (used to confirm/link/download it later).</param>
/// <param name="Status">The asset's lifecycle status name — always <c>Pending</c> immediately after the intent.</param>
/// <param name="ContentType">The declared MIME content type of the object to be uploaded.</param>
/// <param name="UploadUrl">The short-lived, signed URL the client uploads the object to (a secret; never logged).</param>
/// <param name="ExpiresAt">When the signed upload URL stops being valid (UTC).</param>
public sealed record UploadIntentResponse(
    Guid AssetId,
    string Status,
    string ContentType,
    string UploadUrl,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Projects a registered asset and its signed upload URL into the response DTO.</summary>
    public static UploadIntentResponse From(Asset asset, SignedAssetUrl uploadUrl)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(uploadUrl);

        return new UploadIntentResponse(
            asset.Id,
            asset.Status.ToString(),
            asset.ContentType,
            uploadUrl.Url.ToString(),
            uploadUrl.ExpiresAt);
    }

    /// <summary>
    /// Log-safe representation: the asset id, status, content type and expiry only. The signed
    /// <see cref="UploadUrl"/> is a secret (it grants the upload until it expires), so it is deliberately
    /// EXCLUDED — a leaked log line must never be replayable as asset access (threats T4 "Asset leak" and
    /// T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"UploadIntentResponse assetId={AssetId} status={Status} contentType={ContentType} expiresAt={ExpiresAt:O}";
}

/// <summary>
/// Response body of the signed download flow (CORE-AST-004,
/// <c>GET /api/v1/assets/{assetId}/download-url</c>, csv/api_routes.csv "Signed URL after permission
/// check", authorized viewers). It returns the asset's id, lifecycle status and content type plus the
/// short-lived, signed download URL (and its expiry) the authorized caller fetches the object's bytes
/// with. The asset is PRIVATE: the only access this hands out is the single short-lived signed download
/// URL, minted ONLY after the server-side permission check passes (the epic acceptance criterion:
/// "Assets are private by default and accessed only through authorized signed URLs"; threat T4 "Asset
/// leak"). The response carries no internal storage coordinates beyond what the signed URL itself embeds,
/// and no authorization rationale (docs/08_API_CONTRACTS.md; threat T7).
///
/// The <see cref="DownloadUrl"/> is a secret (it embeds the object key and signature); it is delivered to
/// the authorized caller over HTTPS and must never be logged. The auto-generated record <c>ToString</c>
/// is overridden to exclude it (threats T4/T7), exactly as <see cref="UploadIntentResponse"/> excludes its
/// upload URL.
/// </summary>
/// <param name="AssetId">The surrogate id of the asset whose object the URL downloads.</param>
/// <param name="Status">The asset's lifecycle status name — always <c>Available</c> for a downloadable asset.</param>
/// <param name="ContentType">The MIME content type of the stored object.</param>
/// <param name="DownloadUrl">The short-lived, signed URL the client downloads the object from (a secret; never logged).</param>
/// <param name="ExpiresAt">When the signed download URL stops being valid (UTC).</param>
public sealed record DownloadUrlResponse(
    Guid AssetId,
    string Status,
    string ContentType,
    string DownloadUrl,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Projects an asset and its signed download URL into the response DTO.</summary>
    public static DownloadUrlResponse From(Asset asset, SignedAssetUrl downloadUrl)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(downloadUrl);

        return new DownloadUrlResponse(
            asset.Id,
            asset.Status.ToString(),
            asset.ContentType,
            downloadUrl.Url.ToString(),
            downloadUrl.ExpiresAt);
    }

    /// <summary>
    /// Log-safe representation: the asset id, status, content type and expiry only. The signed
    /// <see cref="DownloadUrl"/> is a secret (it grants the download until it expires), so it is
    /// deliberately EXCLUDED — a leaked log line must never be replayable as asset access (threats T4
    /// "Asset leak" and T7 in docs/07_SECURITY_THREAT_MODEL.md).
    /// </summary>
    public override string ToString()
        => $"DownloadUrlResponse assetId={AssetId} status={Status} contentType={ContentType} expiresAt={ExpiresAt:O}";
}

/// <summary>
/// Request body for the asset-link command (CORE-AST-005, <c>POST /api/v1/assets/{assetId}/links</c>,
/// csv/api_routes.csv "Link asset to content block or entity", roles Host/CoHost/Owner/Admin). The route
/// path carries the asset id, so the body supplies the target organization
/// (<see cref="OrganizationSlug"/>, resolved to the tenant by the same token-claim-and-membership check as
/// the reveal command — defence in depth, threat T5) and the linked resource: its generic
/// <see cref="TargetType"/> (ContentBlock or Entity) and its <see cref="TargetId"/> (a resource in the
/// asset's own workspace). The body carries no vertical vocabulary (docs/04_PRODUCT_BOUNDARIES.md) and no
/// storage coordinate (linking never touches the stored object).
/// </summary>
/// <param name="OrganizationSlug">
/// Canonical slug of the organization that owns the asset's workspace, used to resolve the tenant context.
/// </param>
/// <param name="TargetType">
/// The generic kind of resource to link the asset to: <c>ContentBlock</c> or <c>Entity</c>. Parsed by its
/// stable NAME; a missing, numeric or unknown value is a 400.
/// </param>
/// <param name="TargetId">
/// The surrogate id of the target content block / entity, which must exist in the asset's own workspace; a
/// target not in the workspace is hidden as 404.
/// </param>
public sealed record CreateAssetLinkRequest(
    string? OrganizationSlug,
    string? TargetType,
    Guid TargetId);

/// <summary>
/// Response body of the asset-link command (CORE-AST-005). It returns the created link's id, the asset it
/// attaches, the linked target (kind + id) and the creation timestamp. It carries NO storage coordinate
/// and NO authorization rationale (docs/08_API_CONTRACTS.md; threat T7): a link only records that the
/// asset is attached to a resource whose audience visibility the Visibility engine governs — the asset
/// stays private and is still reached only through an authorized signed URL (the epic acceptance
/// criterion; threat T4 "Asset leak").
/// </summary>
/// <param name="LinkId">The surrogate id of the created link.</param>
/// <param name="AssetId">The asset the link attaches.</param>
/// <param name="TargetType">The linked resource kind name (<c>ContentBlock</c>/<c>Entity</c>).</param>
/// <param name="TargetId">The linked resource's surrogate id.</param>
/// <param name="CreatedAt">When the link was created (UTC).</param>
public sealed record AssetLinkResponse(
    Guid LinkId,
    Guid AssetId,
    string TargetType,
    Guid TargetId,
    DateTimeOffset CreatedAt)
{
    /// <summary>Projects a created link into the response DTO.</summary>
    public static AssetLinkResponse From(AssetLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new AssetLinkResponse(
            link.Id,
            link.AssetId,
            link.TargetType.ToString(),
            link.TargetId,
            link.CreatedAt);
    }
}
