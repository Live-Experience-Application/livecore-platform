namespace LiveCore.Api.Assets;

/// <summary>
/// The storage operation a signed URL authorizes (CORE-AST-002, the storage adapter interface story of
/// the "Asset Storage and Authorization" epic). An <see cref="Asset"/>'s binary content lives in private,
/// S3-compatible object storage (docs/12_STORAGE_ASSETS.md; ADR 0006) and is reached ONLY through a
/// short-lived, signed URL after a server-side permission check (the epic acceptance criterion: "Assets
/// are private by default and accessed only through authorized signed URLs"; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). The two operations are the only object accesses the adapter ever
/// signs:
/// <list type="bullet">
///   <item>
///   <see cref="Upload"/> — a write URL that lets a client put the object's bytes directly into the
///   private bucket (the "client uploads to storage" step of docs/12_STORAGE_ASSETS.md's asset
///   lifecycle), consumed by the upload-intent flow (CORE-AST-003).
///   </item>
///   <item>
///   <see cref="Download"/> — a read URL that lets a client get the object's bytes after a permission
///   check (docs/12_STORAGE_ASSETS.md "download URL requires authorization"), consumed by the signed
///   download flow (CORE-AST-004).
///   </item>
/// </list>
/// There is deliberately no "public" or "list" operation: buckets are private by default and object
/// listing is never exposed (docs/12_STORAGE_ASSETS.md "no public object listing"). Object deletion (the
/// cleanup job, CORE-AST-006) is a later story and is not modeled here.
///
/// The integers below are only in-memory discriminators (no ordering meaning); they are never persisted —
/// this enum is a transient detail of a minted <see cref="SignedAssetUrl"/>, not a stored column.
/// </summary>
public enum AssetStorageOperation
{
    /// <summary>
    /// A write access: a signed URL that lets a client upload the object's bytes into its private bucket.
    /// </summary>
    Upload = 1,

    /// <summary>
    /// A read access: a signed URL that lets a client download the object's bytes after authorization.
    /// </summary>
    Download = 2,
}
