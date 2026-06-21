// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { AssetLinkTargetType, AssetStatus } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Assets module contracts (CORE-SDK-001). Assets are private by default and
 * reachable only through a short-lived signed URL minted after a server-side
 * permission check (docs/12_STORAGE_ASSETS.md; threat T4). The signed
 * upload/download URLs are secrets — deliver them over HTTPS and never log them.
 */

/** Request body for `POST /api/v1/assets/upload-intent`. */
export interface CreateUploadIntentRequest {
  /** Canonical slug of the organization that owns the workspace. */
  organizationSlug: string;
  /** The workspace the new asset belongs to. */
  workspaceId: Uuid;
  /** The MIME content type of the object that will be uploaded. */
  contentType: string;
  /**
   * The declared size in bytes of the object that will be uploaded. It is
   * reserved against the workspace's `asset.storage.bytes.max` storage quota at
   * upload-intent, so an upload that would take the workspace over its plan
   * storage limit is rejected server-side (409) before any URL is minted. Must
   * be a positive integer.
   */
  sizeBytes: number;
}

/** Response body of the upload-intent command. */
export interface UploadIntentResponse {
  /** Surrogate id of the registered pending asset. */
  assetId: Uuid;
  /** Lifecycle status — always `Pending` immediately after the intent. */
  status: AssetStatus;
  /** The declared MIME content type of the object to be uploaded. */
  contentType: string;
  /** The short-lived signed URL the client uploads the object to (a secret). */
  uploadUrl: string;
  /** When the signed upload URL stops being valid (UTC). */
  expiresAt: IsoDateTimeString;
}

/** Request body for `POST /api/v1/assets/{assetId}/confirm-upload`. */
export interface ConfirmUploadRequest {
  /** Canonical slug of the organization that owns the asset's workspace. */
  organizationSlug: string;
  /** The confirmed size in bytes of the uploaded object. Must be a non-negative integer. */
  sizeBytes: number;
  /** The confirmed checksum of the uploaded object (for example a hex SHA-256 digest). */
  checksum: string;
}

/**
 * Response body of `POST /api/v1/assets/{assetId}/confirm-upload`. Drives the
 * `Pending` → `Available` transition; the asset stays private and is still reached
 * only through an authorized signed download URL.
 */
export interface ConfirmUploadResponse {
  /** Surrogate id of the confirmed asset. */
  assetId: Uuid;
  /** Lifecycle status — always `Available` after a successful confirm. */
  status: AssetStatus;
  /** The MIME content type of the stored object. */
  contentType: string;
  /** The confirmed, recorded object size in bytes. */
  sizeBytes: number;
  /** The confirmed, recorded object checksum. */
  checksum: string;
  /** When the upload was confirmed (UTC). */
  updatedAt: IsoDateTimeString;
}

/** Response body of `GET /api/v1/assets/{assetId}/download-url`. */
export interface DownloadUrlResponse {
  /** Surrogate id of the asset whose object the URL downloads. */
  assetId: Uuid;
  /** Lifecycle status — always `Available` for a downloadable asset. */
  status: AssetStatus;
  /** The MIME content type of the stored object. */
  contentType: string;
  /** The short-lived signed URL the client downloads the object from (a secret). */
  downloadUrl: string;
  /** When the signed download URL stops being valid (UTC). */
  expiresAt: IsoDateTimeString;
}

/** Request body for `POST /api/v1/assets/{assetId}/links`. */
export interface CreateAssetLinkRequest {
  /** Canonical slug of the organization that owns the asset's workspace. */
  organizationSlug: string;
  /** The generic kind of resource to link the asset to. */
  targetType: AssetLinkTargetType;
  /** The surrogate id of the target, which must exist in the asset's workspace. */
  targetId: Uuid;
}

/** Response body of the asset-link command. */
export interface AssetLinkResponse {
  /** Surrogate id of the created link. */
  linkId: Uuid;
  /** The asset the link attaches. */
  assetId: Uuid;
  /** The linked resource kind name (`ContentBlock`/`Entity`). */
  targetType: AssetLinkTargetType;
  /** The linked resource's surrogate id. */
  targetId: Uuid;
  /** When the link was created (UTC). */
  createdAt: IsoDateTimeString;
}
