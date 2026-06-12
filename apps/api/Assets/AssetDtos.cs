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
