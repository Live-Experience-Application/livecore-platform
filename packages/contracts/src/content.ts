import type { ContentBlockType } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Content module contracts (CORE-SDK-001). A content block carries only its
 * generic kind and payload; whether a participant may see it is computed
 * server-side by the Visibility module (docs/05_MODULE_CONTRACTS.md), never
 * decided by this contract.
 */

/**
 * Request body for `POST /api/v1/scenes/{sceneId}/content-blocks`. The target
 * organization is supplied as the `organizationSlug` query parameter, so it is
 * not part of this body.
 */
export interface CreateContentBlockRequest {
  /** Generic kind of the block (`Text`, `Media` or `Data`). */
  type: ContentBlockType;
  /** The content payload of the block (validated per type, server-side). */
  body: string;
}

/** Response projection of a content block at its current revision. */
export interface ContentBlockResponse {
  /** Surrogate id of the content block. */
  id: Uuid;
  /** Tenant the content block belongs to. */
  organizationId: Uuid;
  /** Workspace the content block belongs to. */
  workspaceId: Uuid;
  /** Scene the content block belongs to. */
  sceneId: Uuid;
  /** Generic kind name of the block (`Text`/`Media`/`Data`). */
  type: ContentBlockType;
  /** The content payload of the current revision. */
  body: string;
  /** Monotonic revision number (`1` at creation). */
  revisionNumber: number;
  /** When the content block was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the content block was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
