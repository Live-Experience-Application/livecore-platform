// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { ContentBlockType } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Content module contracts (CORE-SDK-001). A content block carries only its
 * generic kind and payload; whether a participant may see it is computed
 * server-side by the Visibility module (docs/05_MODULE_CONTRACTS.md), never
 * decided by this contract. The list and by-id read are projected by the caller's
 * workspace role: the host-content roles receive the full {@link ContentBlockResponse}
 * (with the body), every other role the stripped {@link ParticipantContentBlockResponse}
 * (CORE-SDK-006; docs/08_API_CONTRACTS.md "Host DTOs and Participant DTOs are different").
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

/**
 * Audience-safe response projection of a content block returned to the audience
 * roles (Participant/Observer) and the audit role (Auditor) by the list and by-id
 * read routes. It deliberately omits the host-only fields — most importantly the
 * {@link ContentBlockResponse.body} content (which stays host content until a
 * separate reveal; threat T2) — and the tenant/workspace/scene boundary ids, the
 * revision number and the host preparation timestamps, carrying only the
 * non-sensitive id and generic kind.
 */
export interface ParticipantContentBlockResponse {
  /** Surrogate id of the content block; a non-sensitive handle. */
  id: Uuid;
  /** Generic kind name of the block (`Text`/`Media`/`Data`); not the content payload. */
  type: ContentBlockType;
}
