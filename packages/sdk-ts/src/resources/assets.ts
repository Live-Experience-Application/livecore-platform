/**
 * Assets resource group (CORE-SDK-002): the upload-intent, signed-download and
 * linking flows. Assets are private by default and reachable only through a
 * short-lived signed URL minted after a server-side permission check
 * (docs/12_STORAGE_ASSETS.md; threat T4). The signed upload/download URLs on the
 * responses are secrets — use them over HTTPS and never log them.
 */
import type {
  AssetLinkResponse,
  CreateAssetLinkRequest,
  CreateUploadIntentRequest,
  DownloadUrlResponse,
  UploadIntentResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class AssetsClient {
  constructor(private readonly http: HttpClient) {}

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
   * organization slug travels in the body.
   */
  createLink(
    assetId: Uuid,
    request: CreateAssetLinkRequest,
  ): Promise<AssetLinkResponse> {
    return this.http.send<AssetLinkResponse>({
      method: "POST",
      path: `/assets/${encodeURIComponent(assetId)}/links`,
      body: request,
    });
  }
}
