// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
  ExportJobStatus,
  ExportResourceKind,
  ExportScope,
} from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Exports module contracts (CORE-EXP-001). A completed workspace export's produced
 * artifact is its manifest — the per-kind table of contents of what the export
 * covered (counts only, never any exported scene/content body; threats T7/T8).
 * The Core stores no separate export blob in object storage, so the artifact is
 * delivered as an authorized stream — the role-projected manifest in the
 * authenticated, authorized response body — never through a public/static URL
 * (docs/12_STORAGE_ASSETS.md; threats T4/T8). Access is authorized server-side
 * before the artifact is produced; a non-authoring role is denied.
 */

/**
 * Request body for `POST /api/v1/workspaces/{workspaceId}/exports` (CORE-EXP-003):
 * request an async WORKSPACE export. The target workspace is the route path and the
 * export scope is fixed to `Workspace` by the route, so the body carries only the
 * tenant slug. Authorized to the "Export workspace" roles (Owner/Admin/Host).
 */
export interface CreateExportRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
}

/**
 * The async export job minted by `POST /api/v1/workspaces/{workspaceId}/exports`
 * (CORE-EXP-003): the `201` body on a fresh request and the `200` body on an
 * idempotent retry (the ORIGINAL job, never a second one). It is the job's generic,
 * product-neutral identity and lifecycle — the `id` (the `exportId` that
 * {@link ExportArtifactResponse}'s read route then addresses), the tenant/workspace
 * boundaries, the requester, the explicit `scope`, the lifecycle `status` (a fresh
 * request is `Pending` until the worker producer drains it) and the server
 * timestamps. It carries no exported content (threats T7/T8).
 */
export interface ExportJobResponse {
  /** Surrogate id of the export job — the `exportId` the read/download route addresses. */
  id: Uuid;
  /** Tenant the job belongs to. */
  organizationId: Uuid;
  /** Workspace the job belongs to. */
  workspaceId: Uuid;
  /**
   * The user that requested the export, or `null` when anonymized (the requester
   * was later erased).
   */
  requestedByUserProfileId: Uuid | null;
  /** The explicit export scope the job was authorized for (always `Workspace` for this route). */
  scope: ExportScope;
  /** The lifecycle status of the job (a freshly requested job is `Pending`). */
  status: ExportJobStatus;
  /** The generic failure reason when the job failed, otherwise `null`. */
  failureReason: string | null;
  /** When the job was requested (UTC). */
  createdAt: IsoDateTimeString;
  /** When the job's status last changed (UTC). */
  updatedAt: IsoDateTimeString;
}

/** One inventory line of a full export manifest view: a count per resource kind. */
export interface ExportManifestEntryView {
  /** The generic kind of resource counted. */
  kind: ExportResourceKind;
  /** How many resources of `kind` the export covered (non-negative). */
  itemCount: number;
}

/**
 * The full, host/metadata-facing artifact of `GET /api/v1/exports/{exportId}` —
 * the role-projected view returned to an authorized downloader (the "Export
 * workspace" roles Owner/Admin/Host). It carries the per-kind inventory and the
 * tenant/workspace boundary identifiers, never any exported content.
 */
export interface ExportManifestView {
  /** Surrogate id of the manifest. */
  id: Uuid;
  /** The export job that produced this manifest. */
  exportJobId: Uuid;
  /** Tenant the manifest belongs to. */
  organizationId: Uuid;
  /** Workspace the manifest belongs to. */
  workspaceId: Uuid;
  /** The explicit export scope of the producing job (always `Workspace`). */
  scope: ExportScope;
  /** The manifest format version. */
  manifestVersion: number;
  /** When the manifest was produced (UTC). */
  generatedAt: IsoDateTimeString;
  /** The per-kind inventory, in ascending-kind order. */
  entries: ExportManifestEntryView[];
  /** The total number of resources the export covered across all kinds. */
  totalItemCount: number;
}

/**
 * The audience-safe, host-only-field-stripped artifact shape: the manifest's
 * non-sensitive identity and scope only — no inventory, no boundary identifiers.
 * The export read/download route authorizes the artifact to the "Export workspace"
 * roles, so an audience caller is denied (403) rather than handed this shape; it
 * is the contract the role-based projection falls closed to and is exported so a
 * consumer can model the projected union exhaustively.
 */
export interface ExportManifestSummaryView {
  /** Surrogate id of the manifest; a non-sensitive handle. */
  id: Uuid;
  /** The explicit export scope of the producing job. */
  scope: ExportScope;
}

/**
 * The role-projected artifact returned by `GET /api/v1/exports/{exportId}`: the
 * full host/metadata view for an authorized downloader, or the host-only-field
 * -stripped summary the projection falls closed to for any other role.
 */
export type ExportArtifactResponse =
  | ExportManifestView
  | ExportManifestSummaryView;
