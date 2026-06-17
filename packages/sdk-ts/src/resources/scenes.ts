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
  SceneResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
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
   * position is assigned server-side; clients never supply a position.
   */
  create(
    workspaceId: Uuid,
    request: CreateSceneRequest,
  ): Promise<SceneResponse> {
    return this.http.send<SceneResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes`,
      body: request,
    });
  }
}
