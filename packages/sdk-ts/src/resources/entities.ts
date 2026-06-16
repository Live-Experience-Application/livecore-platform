/**
 * Entities resource group (CORE-SDK-006): listing, reading and creating a
 * workspace's generic entities (CORE-ENT-006). The list and by-id read are
 * projected server-side by the caller's workspace role — an entity is content,
 * so the host-content roles receive the full {@link EntityResponse} while every
 * other role receives the stripped {@link ParticipantEntityResponse} — so their
 * return types are the union of both shapes (docs/08_API_CONTRACTS.md). The
 * server authorizes every call; a caller who may not see the tenant or workspace
 * is hidden as `404` (a `LiveCoreApiError`), never `403`.
 */
import type {
  CreateEntityRequest,
  EntityResponse,
  ParticipantEntityResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class EntitiesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/entities` — the workspace's entities,
   * projected by the caller's role.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<EntityResponse[] | ParticipantEntityResponse[]> {
    return this.http.send<EntityResponse[] | ParticipantEntityResponse[]>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entities`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/entities/{entityId}` — one entity by
   * id, projected by the caller's role.
   */
  get(
    workspaceId: Uuid,
    entityId: Uuid,
    params: { organizationSlug: string },
  ): Promise<EntityResponse | ParticipantEntityResponse> {
    return this.http.send<EntityResponse | ParticipantEntityResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entities/${encodeURIComponent(entityId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/entities` — create a generic entity.
   * Its surrogate id is assigned server-side; the referenced `entityTypeId` must
   * address a type in the same workspace. The authoring caller always receives
   * the full host {@link EntityResponse}.
   */
  create(
    workspaceId: Uuid,
    request: CreateEntityRequest,
  ): Promise<EntityResponse> {
    return this.http.send<EntityResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entities`,
      body: request,
    });
  }
}
