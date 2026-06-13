/**
 * Scenes resource group (CORE-SDK-002): listing and creating a workspace's
 * scenes. The list is projected server-side by the caller's workspace role —
 * host/metadata roles receive the full {@link SceneResponse}, audience roles the
 * stripped {@link ParticipantSceneResponse} — so the list return type is the
 * union of both shapes (docs/08_API_CONTRACTS.md).
 */
import type {
  CreateSceneRequest,
  ParticipantSceneResponse,
  SceneResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class ScenesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/scenes` — the workspace's scenes,
   * projected by the caller's role.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SceneResponse[] | ParticipantSceneResponse[]> {
    return this.http.send<SceneResponse[] | ParticipantSceneResponse[]>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/scenes`,
      query: { organizationSlug: params.organizationSlug },
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
