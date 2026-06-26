// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Exports resource group (CORE-EXP-001): the export read/download flow. A
 * completed workspace export's produced artifact is its manifest — the per-kind
 * table of contents of what the export covered (counts only, never any exported
 * content; threats T7/T8). The artifact is delivered as an authorized stream over
 * this authenticated route after a server-side permission check, never through a
 * public/static URL (docs/12_STORAGE_ASSETS.md; threats T4/T8). Access is the
 * "Export workspace" roles (Owner/Admin/Host); a non-authoring role is denied.
 */
import type {
  CreateExportRequest,
  ExportArtifactResponse,
  ExportJobResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import type { IdempotentCreateOptions } from "./idempotency.js";

export class ExportsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `POST /api/v1/workspaces/{workspaceId}/exports` — request an async WORKSPACE
   * export (CORE-EXP-003). Mints a `Pending` export job (returning its `id`, the
   * `exportId` {@link getExport} then reads) that the worker export producer drains
   * into a manifest; the export scope is fixed to `Workspace` by the route, so the
   * request body carries only the tenant slug. Authorized to the "Export workspace"
   * roles (Owner/Admin/Host); a non-authoring role is denied. Pass
   * {@link IdempotentCreateOptions.idempotencyKey} to make the request retry-safe
   * (CORE-DX-004): a retry under the SAME key replays the original export job the
   * server already recorded (`200`) instead of minting a second one; omit it to
   * request unconditionally.
   */
  createExport(
    workspaceId: Uuid,
    request: CreateExportRequest,
    options?: IdempotentCreateOptions,
  ): Promise<ExportJobResponse> {
    return this.http.send<ExportJobResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/exports`,
      body: request,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  /**
   * `GET /api/v1/exports/{exportId}` — retrieve a completed workspace export's
   * artifact (its role-projected manifest), after the server's permission check.
   * The organization slug travels in the query. An incomplete or failed export is
   * `409`; a foreign-tenant/unknown export or a non-member is hidden as `404`; a
   * non-authoring role is `403`.
   */
  getExport(
    exportId: Uuid,
    params: { organizationSlug: string },
  ): Promise<ExportArtifactResponse> {
    return this.http.send<ExportArtifactResponse>({
      method: "GET",
      path: `/exports/${encodeURIComponent(exportId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
