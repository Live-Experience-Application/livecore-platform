// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Scenes resource group (CORE-SDK-002): listing and creating a workspace's
 * scenes. The list is projected server-side by the caller's workspace role —
 * host/metadata roles receive the full {@link SceneResponse}, audience roles the
 * stripped {@link ParticipantSceneResponse} — so the list return type is the
 * union of both shapes (docs/08_API_CONTRACTS.md).
 */
import type {
  CreateSceneRequest,
  PageResponse,
  ParticipantSceneResponse,
  ReorderSceneRequest,
  SceneResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import type { IdempotentCreateOptions } from "./idempotency.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class ScenesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/scenes` — the workspace's scenes,
   * projected by the caller's role, as a bounded page (CORE-DX-003). Pass optional
   * `limit`/`offset` to page; the result is the role-projected `items + hasMore`
   * envelope, never an unbounded array.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<
    PageResponse<SceneResponse> | PageResponse<ParticipantSceneResponse>
  > {
    return this.http.send<
      PageResponse<SceneResponse> | PageResponse<ParticipantSceneResponse>
    >({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/scenes` — create a scene. Its ordering
   * position is assigned server-side; clients never supply a position. Pass
   * {@link IdempotentCreateOptions.idempotencyKey} to make the create retry-safe
   * (CORE-DX-008): a retry under the SAME key replays the original scene the
   * server already dedupes (CORE-DX-004) instead of creating a duplicate; omit it
   * to create unconditionally (the prior behavior).
   */
  create(
    workspaceId: Uuid,
    request: CreateSceneRequest,
    options?: IdempotentCreateOptions,
  ): Promise<SceneResponse> {
    return this.http.send<SceneResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes`,
      body: request,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  /**
   * `GET /api/v1/scenes/{sceneId}` — one scene by id, projected by the caller's
   * role (the same host-vs-participant split as {@link list}); a foreign-tenant,
   * unknown scene or non-member is hidden as `404`.
   */
  get(
    sceneId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SceneResponse | ParticipantSceneResponse> {
    return this.http.send<SceneResponse | ParticipantSceneResponse>({
      method: "GET",
      path: `/scenes/${encodeURIComponent(sceneId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/scenes/{sceneId}/reorder` — move a scene
   * to a target list position. The server re-packs every scene to a contiguous,
   * gap-free order (no client-trusted absolute order) and returns the re-packed
   * scene list. Authorized to the host-capable roles, so the list is the full host
   * {@link SceneResponse} shape; a concurrent reorder is `409`.
   */
  reorder(
    workspaceId: Uuid,
    sceneId: Uuid,
    request: ReorderSceneRequest,
  ): Promise<SceneResponse[]> {
    return this.http.send<SceneResponse[]>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes/${encodeURIComponent(sceneId)}/reorder`,
      body: request,
    });
  }

  /**
   * `DELETE /api/v1/workspaces/{workspaceId}/scenes/{sceneId}` — delete a scene,
   * cascading its content blocks, visibility rules and asset links and re-packing
   * the remaining scene ordering without gaps. Audited. Responds `204 No Content`.
   */
  delete(
    workspaceId: Uuid,
    sceneId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes/${encodeURIComponent(sceneId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
