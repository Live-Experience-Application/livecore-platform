// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Invitations resource group (CORE-INV-002): the authenticated caller's own
 * pending-invitation self-discovery read (csv/api_routes.csv
 * `GET /api/v1/me/invitations`). It lets an onboarding flow discover the workspace
 * invitations addressed to the caller — matched server-side ONLY on the caller's
 * verified email and scoped to the tenants the caller belongs to — then drive the
 * existing {@link WorkspacesClient.acceptInvitation} with the returned
 * `organizationSlug` + `workspaceId`, with no workspace id handed over out of band.
 *
 * The server authorizes and matches every call; a caller with no verified email or
 * no matching invitation simply gets an empty page. The projection is PII-safe: it
 * never carries the token hash, a token or any person's data (threats T6/T7).
 */
import type {
  MyPendingWorkspaceInvitationResponse,
  PageResponse,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class InvitationsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/me/invitations` — the authenticated caller's own PENDING workspace
   * invitations, as a bounded page. Each item carries the `organizationSlug` and
   * `workspaceId` an onboarding flow passes to {@link WorkspacesClient.acceptInvitation}.
   * Matching is server-side on the caller's verified email only; a caller with no
   * verified email (or no matching invitation) gets an empty page. Pass optional
   * `limit`/`offset` to page.
   */
  listMine(
    params: PageParams = {},
  ): Promise<PageResponse<MyPendingWorkspaceInvitationResponse>> {
    return this.http.send<PageResponse<MyPendingWorkspaceInvitationResponse>>({
      method: "GET",
      path: "/me/invitations",
      query: pageQuery(params),
    });
  }
}
