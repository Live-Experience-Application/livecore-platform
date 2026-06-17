// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Entity-types resource group (CORE-ENT-007): listing, reading and defining a
 * workspace's generic entity types under
 * `/api/v1/workspaces/{workspaceId}/entity-types`. An entity type is how a
 * vertical maps its domain onto Core — the data-driven definition of a kind of
 * entity (template key plus field/type metadata, the template boundary).
 *
 * Unlike the entities resource, an entity type is an authoring/schema artifact
 * rather than audience content, so there is a single {@link EntityTypeResponse}
 * shape (no host-vs-participant projection) and all three routes are authorized to
 * the authoring roles only. The server authorizes every call; a caller who may not
 * see the tenant or workspace is hidden as `404` (a `LiveCoreApiError`), a
 * non-authoring member is `403`.
 */
import type {
  CreateEntityTypeRequest,
  EntityTypeResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class EntityTypesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/entity-types` — the workspace's entity
   * types, in canonical type-key order.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<EntityTypeResponse[]> {
    return this.http.send<EntityTypeResponse[]>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entity-types`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/entity-types/{entityTypeId}` — one
   * entity type by id.
   */
  get(
    workspaceId: Uuid,
    entityTypeId: Uuid,
    params: { organizationSlug: string },
  ): Promise<EntityTypeResponse> {
    return this.http.send<EntityTypeResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entity-types/${encodeURIComponent(entityTypeId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/entity-types` — define a generic
   * entity type. Its surrogate id is assigned server-side; the `typeKey` must be
   * unique within the workspace (a key the workspace already defines is a `409`).
   */
  create(
    workspaceId: Uuid,
    request: CreateEntityTypeRequest,
  ): Promise<EntityTypeResponse> {
    return this.http.send<EntityTypeResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/entity-types`,
      body: request,
    });
  }
}
