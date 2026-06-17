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
import type { ExportArtifactResponse, Uuid } from "@livecore/contracts";

import type { HttpClient } from "../http.js";

export class ExportsClient {
  constructor(private readonly http: HttpClient) {}

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
