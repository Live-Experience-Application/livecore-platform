// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Templates module contracts (CORE-SDK-006). A template is reusable vertical
 * scaffolding stored as DATA (the template boundary, docs/04_PRODUCT_BOUNDARIES.md):
 * a stable dotted-slug template key, a positive version and an opaque JSON
 * definition document, never inspected for vocabulary by Core. These DTOs back the
 * organization-scoped template create/list/by-id-read routes under
 * `/api/v1/organizations/{organizationSlug}/templates`.
 */

/**
 * Request body for `POST /api/v1/organizations/{organizationSlug}/templates`. The
 * target organization is taken from the route path, so this body carries only the
 * template content. The created template is always organization-scoped (never
 * global); the (`templateKey`, `version`) pair is unique within the organization.
 */
export interface CreateTemplateRequest {
  /** The template's stable, canonical natural key (a lower-case dotted slug). */
  templateKey: string;
  /**
   * The positive integer template version (part of the per-scope natural key).
   * Optional: when omitted the template is created at version `1`.
   */
  version?: number;
  /**
   * The template content as an opaque JSON document (template-/host-supplied data):
   * a top-level template key plus a non-empty entity-types array. Must be
   * well-formed, structurally valid JSON within the size bound.
   */
  definition: string;
}

/**
 * Response projection of a template. There is exactly ONE template shape — a
 * template is an authoring/registry artifact, not audience content, so all three
 * routes are authorized to Owner/Admin only and there is no host-vs-participant
 * split. The org-scoped routes only ever return organization-scoped templates, so
 * {@link organizationId} is always the owning tenant.
 */
export interface TemplateResponse {
  /** Surrogate id of the template. */
  id: Uuid;
  /** Tenant the template belongs to (always set; never a global template). */
  organizationId: Uuid;
  /** The template's canonical natural key (unique within the org with the version). */
  templateKey: string;
  /** The template's positive integer version. */
  version: number;
  /** The template content (an opaque JSON document). */
  definition: string;
  /** When the template was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the template was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
