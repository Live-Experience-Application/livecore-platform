/**
 * Content resource group (CORE-SDK-002): creating the content blocks shown
 * within a scene. The block body is validated per type server-side before it is
 * stored; an invalid or oversize body is rejected with `400`
 * (docs/08_API_CONTRACTS.md). The scene's organization is the required
 * `organizationSlug` query parameter, so it is not part of the request body.
 */
import type {
  ContentBlockResponse,
  CreateContentBlockRequest,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class ContentClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `POST /api/v1/scenes/{sceneId}/content-blocks` — create a content block at
   * its initial revision.
   */
  createBlock(
    sceneId: Uuid,
    params: { organizationSlug: string },
    request: CreateContentBlockRequest,
  ): Promise<ContentBlockResponse> {
    return this.http.send<ContentBlockResponse>({
      method: "POST",
      path: `/scenes/${encodeURIComponent(sceneId)}/content-blocks`,
      query: { organizationSlug: params.organizationSlug },
      body: request,
    });
  }
}
