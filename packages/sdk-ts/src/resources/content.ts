// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Content resource group (CORE-SDK-002, extended in CORE-SDK-006): listing,
 * reading, creating and deleting the content blocks shown within a scene. The
 * block body is validated per type server-side before it is stored; an invalid or
 * oversize body is rejected with `400` (docs/08_API_CONTRACTS.md). The scene's
 * organization is the required `organizationSlug` query parameter, so it is not
 * part of a request body. The list and by-id read are projected by the caller's
 * workspace role — the host-content roles receive the full {@link ContentBlockResponse}
 * (with the body), every other role the stripped {@link ParticipantContentBlockResponse}
 * (the body stays host content until a separate reveal; threat T2) — so their
 * return types are the union of both shapes.
 */
import type {
  ContentBlockResponse,
  CreateContentBlockRequest,
  PageResponse,
  ParticipantContentBlockResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import type { IdempotentCreateOptions } from "./idempotency.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class ContentClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/scenes/{sceneId}/content-blocks` — the scene's content blocks in
   * deterministic id order, projected by the caller's role, as a bounded page
   * (CORE-DX-003). Pass optional `limit`/`offset` to page; the result is the
   * role-projected `items + hasMore` envelope, never an unbounded array.
   */
  listBlocks(
    sceneId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<
    | PageResponse<ContentBlockResponse>
    | PageResponse<ParticipantContentBlockResponse>
  > {
    return this.http.send<
      | PageResponse<ContentBlockResponse>
      | PageResponse<ParticipantContentBlockResponse>
    >({
      method: "GET",
      path: `/scenes/${encodeURIComponent(sceneId)}/content-blocks`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `GET /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}` — one content
   * block within its scene, projected by the caller's role through the same
   * projection as {@link listBlocks} (so the by-id read never diverges from the
   * list). A foreign-tenant, wrong-scene, unknown block or non-member is hidden
   * as `404`.
   */
  getBlock(
    sceneId: Uuid,
    contentBlockId: Uuid,
    params: { organizationSlug: string },
  ): Promise<ContentBlockResponse | ParticipantContentBlockResponse> {
    return this.http.send<
      ContentBlockResponse | ParticipantContentBlockResponse
    >({
      method: "GET",
      path: `/scenes/${encodeURIComponent(sceneId)}/content-blocks/${encodeURIComponent(contentBlockId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/scenes/{sceneId}/content-blocks` — create a content block at
   * its initial revision. The authoring caller always receives the full host
   * {@link ContentBlockResponse}. Pass
   * {@link IdempotentCreateOptions.idempotencyKey} to make the create retry-safe
   * (CORE-DX-008): a retry under the SAME key replays the original content block
   * the server already dedupes (CORE-DX-004) instead of creating a duplicate;
   * omit it to create unconditionally (the prior behavior).
   */
  createBlock(
    sceneId: Uuid,
    params: { organizationSlug: string },
    request: CreateContentBlockRequest,
    options?: IdempotentCreateOptions,
  ): Promise<ContentBlockResponse> {
    return this.http.send<ContentBlockResponse>({
      method: "POST",
      path: `/scenes/${encodeURIComponent(sceneId)}/content-blocks`,
      query: { organizationSlug: params.organizationSlug },
      body: request,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  /**
   * `DELETE /api/v1/scenes/{sceneId}/content-blocks/{contentBlockId}` — delete a
   * content block, cascading its dependent visibility rules and asset links.
   * Audited. Responds `204 No Content`.
   */
  deleteBlock(
    sceneId: Uuid,
    contentBlockId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/scenes/${encodeURIComponent(sceneId)}/content-blocks/${encodeURIComponent(contentBlockId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
