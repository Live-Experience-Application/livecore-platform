// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

namespace LiveCore.Api.Assets;

/// <summary>
/// A storage operation the <see cref="IAssetStorage"/> adapter performs on an <see cref="Asset"/>'s object
/// (CORE-AST-002 introduced the two signed-URL operations; CORE-AST-006, the cleanup job, adds the
/// server-side delete). An <see cref="Asset"/>'s binary content lives in private, S3-compatible object
/// storage (docs/12_STORAGE_ASSETS.md; ADR 0006) and is reached ONLY through a short-lived, signed URL
/// after a server-side permission check (the epic acceptance criterion: "Assets are private by default
/// and accessed only through authorized signed URLs"; threat T4 "Asset leak" in
/// docs/07_SECURITY_THREAT_MODEL.md). The two access operations the adapter ever SIGNS a URL for are:
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
/// In addition, the adapter performs one SERVER-SIDE operation that hands no URL to any client:
/// <list type="bullet">
///   <item>
///   <see cref="Delete"/> — removes the object from its private bucket. This is NOT a signed-URL operation:
///   the deployment-supplied adapter deletes the object directly with its own credentials. It is used only
///   by the background asset cleanup job (CORE-AST-006) to reclaim the orphaned objects of abandoned
///   pending upload intents; it never serves bytes and never widens access.
///   </item>
/// </list>
/// There is deliberately no "public" or "list" operation: buckets are private by default and object
/// listing is never exposed (docs/12_STORAGE_ASSETS.md "no public object listing").
///
/// The integers below are only in-memory discriminators (no ordering meaning); they are never persisted —
/// this enum is a transient detail of a minted <see cref="SignedAssetUrl"/> or a delete request, not a
/// stored column.
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

    /// <summary>
    /// A server-side delete: the adapter removes the object from its private bucket directly (no client URL
    /// is produced). Used only by the asset cleanup job (CORE-AST-006) to remove orphaned objects.
    /// </summary>
    Delete = 3,
}
