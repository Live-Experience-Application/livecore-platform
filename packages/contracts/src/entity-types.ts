import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Entity-type module contracts (CORE-ENT-007). An entity TYPE is how a vertical
 * maps its domain onto Core: the generic, data-driven definition of a kind of
 * entity — a stable template `typeKey` plus field/type metadata (the template
 * boundary, docs/04_PRODUCT_BOUNDARIES.md). These are the request/response shapes
 * for the entity-type create/list/by-id-read routes under
 * `/api/v1/workspaces/{workspaceId}/entity-types`.
 *
 * Unlike the entity contracts (CORE-ENT-006), there is exactly ONE entity-type
 * shape: an entity type is an authoring/schema artifact rather than audience
 * content, so all three routes are authorized to the authoring roles
 * (Owner/Admin/Host/CoHost) only and there is no host-vs-participant projection.
 * The shapes are generic and product-neutral: the type's `typeKey`, `displayName`
 * and `attributeSchema` are data, never inspected for vocabulary (the template
 * boundary).
 */

/** Request body for `POST /api/v1/workspaces/{workspaceId}/entity-types`. */
export interface CreateEntityTypeRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /**
   * The type's stable, canonical natural key, unique within the route's
   * workspace. Canonicalized server-side to a lower-case dash-separated slug, so
   * a near-match or re-cased value resolves to the same key; a key the workspace
   * already defines is a `409`.
   */
  typeKey: string;
  /** Human-readable label of the type (template-/host-supplied data). */
  displayName: string;
  /**
   * The type's template-defined attribute schema as a JSON document
   * (template-/host-supplied data). Optional: when omitted or blank the type is
   * created with an empty JSON object (`{}`); when provided it must be well-formed
   * JSON within the size bound.
   */
  attributeSchema?: string;
}

/**
 * Response projection of an entity type, returned to the authoring roles
 * (Owner/Admin/Host/CoHost) on the create, list and by-id read routes. There is a
 * single entity-type shape (an entity type is an authoring/schema artifact, not
 * audience content): no audience role receives an entity type, so there is no
 * stripped participant variant.
 */
export interface EntityTypeResponse {
  /** Surrogate id of the entity type (assigned server-side). */
  id: Uuid;
  /** Tenant the entity type belongs to. */
  organizationId: Uuid;
  /** Workspace the entity type belongs to. */
  workspaceId: Uuid;
  /** The type's canonical natural key (unique within the workspace). */
  typeKey: string;
  /** Human-readable label of the entity type. */
  displayName: string;
  /** The type's template-defined attribute schema (a JSON document). */
  attributeSchema: string;
  /** When the entity type was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the entity type was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
