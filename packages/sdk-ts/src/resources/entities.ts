// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
  PageResponse,
  ParticipantEntityResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class EntitiesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/entities` — the workspace's entities,
   * projected by the caller's role, as a bounded page (CORE-DX-003). Pass optional
   * `limit`/`offset` to page; the result is the role-projected `items + hasMore`
   * envelope, never an unbounded array.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<
    PageResponse<EntityResponse> | PageResponse<ParticipantEntityResponse>
  > {
    return this.http.send<
      PageResponse<EntityResponse> | PageResponse<ParticipantEntityResponse>
    >({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entities`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
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

  /**
   * `DELETE /api/v1/workspaces/{workspaceId}/entities/{entityId}` — delete an
   * entity, cascading its dependent relationship edges, visibility rules and asset
   * links. Audited. Responds `204 No Content`.
   */
  delete(
    workspaceId: Uuid,
    entityId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entities/${encodeURIComponent(entityId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `DELETE /api/v1/workspaces/{workspaceId}/entity-relationships/{relationshipId}`
   * — remove a directed entity relationship edge (workspace-scoped). The edge's
   * endpoints are unaffected. Responds `204 No Content`; an unknown edge is hidden
   * as `404`.
   */
  deleteRelationship(
    workspaceId: Uuid,
    relationshipId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entity-relationships/${encodeURIComponent(relationshipId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
