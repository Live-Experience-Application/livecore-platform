import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Scenes module contracts (CORE-SDK-001). The scene list is projected by the
 * caller's workspace role: host-capable/metadata roles receive the full
 * {@link SceneResponse}, while audience roles receive the stripped, audience-safe
 * {@link ParticipantSceneResponse} (docs/08_API_CONTRACTS.md "Host DTOs and
 * Participant DTOs are different").
 */

/** Request body for `POST /api/v1/workspaces/{workspaceId}/scenes`. */
export interface CreateSceneRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /** Human-readable display title of the new scene. */
  title: string;
}

/** Full host-facing response projection of a scene. */
export interface SceneResponse {
  /** Surrogate id of the scene. */
  id: Uuid;
  /** Tenant the scene belongs to. */
  organizationId: Uuid;
  /** Workspace the scene belongs to. */
  workspaceId: Uuid;
  /** Human-readable display title of the scene. */
  title: string;
  /** Ordering position of the scene within its workspace (assigned server-side). */
  order: number;
  /** When the scene was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the scene was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}

/**
 * Audience-safe response projection of a scene returned to the audience roles
 * (Participant/Observer). It deliberately omits the internal tenant/workspace
 * boundary ids and the host preparation timestamps, and carries no hidden
 * content or authorization rationale (docs/08_API_CONTRACTS.md).
 */
export interface ParticipantSceneResponse {
  /** Surrogate id of the scene; a non-sensitive correlation handle. */
  id: Uuid;
  /** Human-readable display title of the scene. */
  title: string;
  /** Ordering position of the scene within its workspace. */
  order: number;
}
