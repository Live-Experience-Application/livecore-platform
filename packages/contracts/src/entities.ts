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

/**
 * Query criteria for `GET /api/v1/workspaces/{workspaceId}/entities/search`
 * (CORE-ENT-009): server-side filtered entity search WITH visibility filtering,
 * so a vertical can filter server-side instead of list-then-filter client-side.
 *
 * Every filter is OPTIONAL and combines with AND. The result is projected by the
 * caller's role exactly like the entity list — host-content roles get the full
 * {@link EntityResponse}; an audience caller gets the stripped
 * {@link ParticipantEntityResponse} for only the entities the server reveals to
 * them in the named `sessionId` (a participant search never returns an unrevealed
 * entity). The calling participant is resolved server-side from the principal,
 * never supplied here. The `name` term is opaque data matched as a plain substring
 * (the template boundary), never inspected for vocabulary.
 */
export interface EntitySearchCriteria {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /**
   * Optional case-insensitive name substring to match; omit (or pass an empty
   * string) for no name filter.
   */
  name?: string;
  /** Optional entity type to restrict the search to; omit for all types. */
  entityTypeId?: Uuid;
  /**
   * Optional session the audience visibility filter is bounded by (a reveal is
   * session-scoped). It is consulted only for an audience caller: a host sees every
   * matching entity regardless, and an audience caller without an identified
   * session gets the fail-closed empty result.
   */
  sessionId?: Uuid;
}

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

/**
 * Request body for `POST /api/v1/workspaces/{workspaceId}/entity-relationships`
 * (CORE-ENT-008): create a directed entity relationship edge between two entities
 * in the route's workspace.
 *
 * Both endpoint ids must address entities IN THE SAME WORKSPACE (resolved
 * server-side — the same-workspace coupling the database foreign keys cannot
 * enforce); an endpoint that does not resolve there is a `400`, a self-loop
 * (source equals target) is a `400`, and a duplicate of the same directed edge of
 * the same kind is a `409`. The edge's surrogate id is assigned server-side.
 */
export interface CreateEntityRelationshipRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /** Surrogate id of the entity the directed edge points FROM. */
  sourceEntityId: Uuid;
  /**
   * Surrogate id of the entity the directed edge points TO. Must be distinct from
   * `sourceEntityId` (no self-loop).
   */
  targetEntityId: Uuid;
  /**
   * Generic, canonical kind/label of the edge (template-/host-supplied data): a
   * lower-case dash-separated slug, stored verbatim and never inspected for
   * vocabulary (the template boundary).
   */
  relationshipKind: string;
}

/**
 * Response projection of a directed entity relationship edge (CORE-ENT-008),
 * returned by the create route (`POST .../entity-relationships`) and the list read
 * (`GET .../entity-relationships`).
 *
 * An edge is a structural/authoring graph artifact (no free-form content), so —
 * like the entity-type reads — the relationship reads are restricted to the
 * authoring roles (Owner/Admin/Host/CoHost) and there is a SINGLE projection (no
 * host-vs-participant split). The shape carries identifiers, the tenant/workspace
 * boundaries, the directed endpoints, the generic kind and the server timestamps;
 * it carries no authorization rationale (docs/08_API_CONTRACTS.md).
 */
export interface EntityRelationshipResponse {
  /** Surrogate id of the relationship (assigned server-side). */
  id: Uuid;
  /** Tenant the relationship belongs to. */
  organizationId: Uuid;
  /** Workspace the relationship belongs to. */
  workspaceId: Uuid;
  /** The entity the directed edge points FROM. */
  sourceEntityId: Uuid;
  /** The entity the directed edge points TO. */
  targetEntityId: Uuid;
  /** The generic, canonical kind/label of the edge. */
  relationshipKind: string;
  /** When the relationship was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the relationship was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
