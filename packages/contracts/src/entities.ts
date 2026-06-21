// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Entities module contracts (CORE-ENT-006). A vertical authors its world through
 * generic entities; these are the request/response shapes for the entity
 * create/list/by-id-read routes under
 * `/api/v1/workspaces/{workspaceId}/entities`.
 *
 * An entity IS content, so the list and read are projected by the caller's
 * workspace role: the host-content roles (Owner/Admin/Host/CoHost) receive the
 * full {@link EntityResponse} (including its attribute-values content), while the
 * audience roles (Participant/Observer), the audit role (Auditor) and any other
 * role receive the stripped, audience-safe {@link ParticipantEntityResponse}
 * (docs/08_API_CONTRACTS.md "Host DTOs and Participant DTOs are different";
 * "Participant DTOs must not contain hidden content fields"). The shapes are
 * generic and product-neutral: the entity's `name` and `attributeValues` are
 * data, never inspected for vocabulary (the template boundary).
 */

/** Request body for `POST /api/v1/workspaces/{workspaceId}/entities`. */
export interface CreateEntityRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /**
   * Surrogate id of the entity type the new entity is an instance of. Must
   * address a type in the route's workspace (resolved server-side; a type that
   * does not resolve there is a `400`).
   */
  entityTypeId: Uuid;
  /** Human-readable label of the new entity (template-/host-supplied data). */
  name: string;
  /**
   * The entity's attribute values as a JSON document (template-/host-supplied
   * data). Optional: when omitted or blank the entity is created with an empty
   * JSON object (`{}`); when provided it must be well-formed JSON within the
   * size bound.
   */
  attributeValues?: string;
}

/**
 * Full host-facing response projection of an entity, returned to the
 * host-content roles (Owner/Admin/Host/CoHost) and as the create response.
 */
export interface EntityResponse {
  /** Surrogate id of the entity (assigned server-side). */
  id: Uuid;
  /** Tenant the entity belongs to. */
  organizationId: Uuid;
  /** Workspace the entity belongs to. */
  workspaceId: Uuid;
  /** The entity type this entity is an instance of. */
  entityTypeId: Uuid;
  /** Human-readable label of the entity. */
  name: string;
  /** The entity's attribute values (a JSON document). */
  attributeValues: string;
  /** When the entity was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the entity was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}

/**
 * Audience-safe response projection of an entity returned to the audience roles
 * (Participant/Observer), the audit role (Auditor) and any other role. It carries
 * only the entity's non-sensitive identity (id, name and an audience-safe
 * entity-type discriminator); it deliberately omits the attribute-values content,
 * the internal tenant/workspace/type-surrogate ids and the host preparation
 * timestamps, and carries no hidden content or authorization rationale
 * (docs/08_API_CONTRACTS.md; threats T2/T7).
 */
export interface ParticipantEntityResponse {
  /** Surrogate id of the entity; a non-sensitive correlation handle. */
  id: Uuid;
  /** Human-readable label of the entity. */
  name: string;
  /**
   * Audience-safe entity-type discriminator (CORE-APROJ-003): the entity type's
   * stable, lower-case natural key (the `EntityType.TypeKey` slug), so an audience
   * surface can group or filter entities by kind from the list alone with no host
   * read. It is DATA, not host content — a canonical slug, never inspected for
   * vocabulary (the template boundary) — and is DISTINCT from the host-only
   * surrogate `entityTypeId` ({@link EntityResponse}), which stays omitted. Empty
   * when the type key cannot be resolved (a degrade, not an error).
   */
  entityTypeKey: string;
}
