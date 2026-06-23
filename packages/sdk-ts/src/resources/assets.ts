// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Assets resource group (CORE-SDK-002): the upload-intent, signed-download and
 * linking flows. Assets are private by default and reachable only through a
 * short-lived signed URL minted after a server-side permission check
 * (docs/12_STORAGE_ASSETS.md; threat T4). The signed upload/download URLs on the
 * responses are secrets — use them over HTTPS and never log them.
 */
import type {
  AssetLinkResponse,
  AssetLinkTargetType,
  AssetResponse,
  ConfirmUploadRequest,
  ConfirmUploadResponse,
  CreateAssetLinkRequest,
  CreateUploadIntentRequest,
  DownloadUrlResponse,
  PageResponse,
  UploadIntentResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import type { IdempotentCreateOptions } from "./idempotency.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class AssetsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/assets` — the workspace's HOST assets, as
   * a bounded page (CORE-ALC-003). The full host {@link AssetResponse} metadata of
   * every asset in the workspace (all lifecycle statuses), so an authoring surface can
   * enumerate a workspace's uploaded assets to re-attach, reveal or delete them across
   * page loads. This is the HOST projection — distinct from the audience-safe
   * attachments on the participant visible feed (CORE-ALC-002). Assets are host-only,
   * so a caller who is not a host-content role (and a foreign/unknown workspace) is
   * hidden as `404`, never `403`. Pass optional `limit`/`offset` to page.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<AssetResponse>> {
    return this.http.send<PageResponse<AssetResponse>>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/assets`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `GET /api/v1/assets/by-target/{targetType}/{targetId}` — the HOST assets
   * attached to ONE target resource (a content block or entity), as a bounded page
   * (CORE-ALC-004). It returns the same full host {@link AssetResponse} projection as
   * {@link list} for every asset LINKED to the target, in every lifecycle status, so
   * a host authoring surface focused on one resource can see and manage its
   * attachments without enumerating the whole workspace. A content block / entity
   * cannot be addressed by id alone, so the target's `workspaceId` is required
   * alongside `organizationSlug`. This is the HOST counterpart of the audience-safe
   * per-resource attachments on the participant visible feed (CORE-ALC-002) and
   * complements, never replaces, it. Authorized to the host-content roles
   * (Owner/Admin/Host/CoHost): a non-host member is denied `403`, a target outside
   * the caller's tenant/workspace is hidden as `404`, and a target with no links is
   * an empty page (never an error). Pass optional `limit`/`offset` to page.
   */
  listForResource(
    targetType: AssetLinkTargetType,
    targetId: Uuid,
    params: { organizationSlug: string; workspaceId: Uuid } & PageParams,
  ): Promise<PageResponse<AssetResponse>> {
    return this.http.send<PageResponse<AssetResponse>>({
      method: "GET",
      path: `/assets/by-target/${encodeURIComponent(targetType)}/${encodeURIComponent(targetId)}`,
      query: {
        organizationSlug: params.organizationSlug,
        workspaceId: params.workspaceId,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `POST /api/v1/assets/upload-intent` — register a pending asset and return a
   * short-lived signed upload URL. The organization slug travels in the body.
   */
  createUploadIntent(
    request: CreateUploadIntentRequest,
  ): Promise<UploadIntentResponse> {
    return this.http.send<UploadIntentResponse>({
      method: "POST",
      path: "/assets/upload-intent",
      body: request,
    });
  }

  /**
   * `POST /api/v1/assets/{assetId}/confirm-upload` — confirm a successful upload:
   * drive the `Pending` → `Available` transition with the uploaded size and
   * checksum, so the asset becomes downloadable. Audited; a non-`Pending` asset is
   * `409`. The organization slug travels in the body.
   */
  confirmUpload(
    assetId: Uuid,
    request: ConfirmUploadRequest,
  ): Promise<ConfirmUploadResponse> {
    return this.http.send<ConfirmUploadResponse>({
      method: "POST",
      path: `/assets/${encodeURIComponent(assetId)}/confirm-upload`,
      body: request,
    });
  }

  /**
   * `GET /api/v1/assets/{assetId}/download-url` — a short-lived signed download
   * URL for an `Available` asset, after the server's permission check. A still
   * `Pending` asset is `409`.
   */
  getDownloadUrl(
    assetId: Uuid,
    params: { organizationSlug: string },
  ): Promise<DownloadUrlResponse> {
    return this.http.send<DownloadUrlResponse>({
      method: "GET",
      path: `/assets/${encodeURIComponent(assetId)}/download-url`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/assets/{assetId}/links` — link an asset to a content block or
   * entity in its own workspace. Linking never makes an asset public; it only
   * records the attachment whose audience visibility the server governs. The
   * organization slug travels in the body. Pass
   * {@link IdempotentCreateOptions.idempotencyKey} to make the create retry-safe
   * (CORE-DX-008): a retry under the SAME key replays the original asset link the
   * server already dedupes (CORE-DX-004) instead of creating a duplicate; omit it
   * to create unconditionally (the prior behavior).
   */
  createLink(
    assetId: Uuid,
    request: CreateAssetLinkRequest,
    options?: IdempotentCreateOptions,
  ): Promise<AssetLinkResponse> {
    return this.http.send<AssetLinkResponse>({
      method: "POST",
      path: `/assets/${encodeURIComponent(assetId)}/links`,
      body: request,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  /**
   * `DELETE /api/v1/assets/{assetId}/links/{linkId}` — unlink an asset from a
   * content block or entity: remove only the link row (the asset and its target are
   * unaffected). Responds `204 No Content`; an unknown link is hidden as `404`.
   */
  deleteLink(
    assetId: Uuid,
    linkId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/assets/${encodeURIComponent(assetId)}/links/${encodeURIComponent(linkId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `DELETE /api/v1/assets/{assetId}` — delete an asset: remove its links and the
   * underlying storage object (object before row). Audited; `503` if storage is
   * unconfigured. Responds `204 No Content`; an unknown asset is hidden as `404`.
   */
  delete(assetId: Uuid, params: { organizationSlug: string }): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/assets/${encodeURIComponent(assetId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
