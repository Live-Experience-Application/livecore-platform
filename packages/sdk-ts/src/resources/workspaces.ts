// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Workspaces resource group (CORE-SDK-002): the tenant-scoped workspace
 * create/read/update and member-invite routes (csv/api_routes.csv). The server
 * authorizes every call; a caller who may not see the tenant or workspace is
 * hidden as `404` (a `LiveCoreApiError`), never `403`
 * (docs/06_AUTHORIZATION_MATRIX.md).
 */
import type {
  AcceptWorkspaceInvitationRequest,
  CreateWorkspaceRequest,
  InviteWorkspaceMemberRequest,
  PageResponse,
  PendingWorkspaceInvitationResponse,
  UpdateWorkspaceMemberRoleRequest,
  UpdateWorkspaceRequest,
  Uuid,
  WorkspaceInvitationResponse,
  WorkspaceMemberResponse,
  WorkspaceMemberRosterEntryResponse,
  WorkspaceResponse,
} from "@livecore/contracts";

import type { HttpClient, SdkResponse } from "../http.js";
import type { IdempotentCreateOptions } from "./idempotency.js";
import { pageQuery, type PageParams } from "./pagination.js";

/** Options for a conditional workspace write (CORE-DX-002). */
export interface ConditionalWriteOptions {
  /**
   * The weak `ETag` (or its bare `version` value) the caller last read for the
   * workspace, sent as `If-Match`. The server refuses a stale value with `412`
   * BEFORE the write, so a GET-then-PUT across HTTP cannot silently clobber a
   * concurrent change; omit it to write unconditionally (the current behavior).
   */
  ifMatch?: string;
}

export class WorkspacesClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces` — workspaces the caller is a member of, as a bounded
   * page (CORE-DX-003). Pass optional `limit`/`offset` to page; the result is the
   * `items + hasMore` envelope, never an unbounded array.
   */
  list(
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<WorkspaceResponse>> {
    return this.http.send<PageResponse<WorkspaceResponse>>({
      method: "GET",
      path: "/workspaces",
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `POST /api/v1/workspaces` — create a workspace (organization Owner/Admin).
   * Pass {@link IdempotentCreateOptions.idempotencyKey} to make the create
   * retry-safe (CORE-DX-008): a retry under the SAME key replays the original
   * workspace the server already dedupes (CORE-DX-004) instead of creating a
   * duplicate; omit it to create unconditionally (the prior behavior).
   */
  create(
    request: CreateWorkspaceRequest,
    options?: IdempotentCreateOptions,
  ): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "POST",
      path: "/workspaces",
      body: request,
      idempotencyKey: options?.idempotencyKey,
    });
  }

  /** `GET /api/v1/workspaces/{workspaceId}` — a workspace by id. */
  get(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}` — a workspace by id together with its
   * weak `ETag` (CORE-DX-002). Pass the returned `etag` as
   * {@link ConditionalWriteOptions.ifMatch} to {@link update} (or another mutation)
   * to make that write conditional on the version you just read. The same tag is
   * also available on the body as `data.version`.
   */
  getWithETag(
    workspaceId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SdkResponse<WorkspaceResponse>> {
    return this.http.sendWithETag<WorkspaceResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `PUT /api/v1/workspaces/{workspaceId}` — rename a workspace. Pass
   * {@link ConditionalWriteOptions.ifMatch} to make the rename conditional on the
   * version last read (a stale value is refused with `412`); omit it to rename
   * unconditionally.
   */
  update(
    workspaceId: Uuid,
    request: UpdateWorkspaceRequest,
    options?: ConditionalWriteOptions,
  ): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "PUT",
      path: `/workspaces/${encodeURIComponent(workspaceId)}`,
      body: request,
      ifMatch: options?.ifMatch,
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/archive` — archive a workspace (a soft,
   * terminal `Active` → `Archived` transition; Owner-only). Pass
   * {@link ConditionalWriteOptions.ifMatch} to make the archive conditional on the
   * version last read (a stale value is refused with `412`); omit it to archive
   * unconditionally. Returns the archived workspace with its new version.
   */
  archive(
    workspaceId: Uuid,
    params: { organizationSlug: string },
    options?: ConditionalWriteOptions,
  ): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/archive`,
      query: { organizationSlug: params.organizationSlug },
      ifMatch: options?.ifMatch,
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/members` — create a member invitation.
   * The one-time plaintext token is on the response exactly once; treat it as a
   * secret and never log it (docs/07_SECURITY_THREAT_MODEL.md threat T6).
   */
  inviteMember(
    workspaceId: Uuid,
    request: InviteWorkspaceMemberRequest,
  ): Promise<WorkspaceInvitationResponse> {
    return this.http.send<WorkspaceInvitationResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/members`,
      body: request,
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/members` — the workspace's member ROSTER,
   * as a bounded page (Owner/Admin). Each entry carries the membership `id` (the id
   * {@link removeMember} requires), the `userProfileId`, the generic `role` and the
   * audience-safe `displayName`; the projection never carries an email, token or auth
   * rationale (threats T6/T7). The roster discloses the membership list, so a caller
   * who is not an Owner/Admin (and a foreign/unknown workspace) is hidden as `404`,
   * never `403`. Pass optional `limit`/`offset` to page.
   */
  listMembers(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<WorkspaceMemberRosterEntryResponse>> {
    return this.http.send<PageResponse<WorkspaceMemberRosterEntryResponse>>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/members`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/invitations` — the workspace's PENDING
   * invitations, as a bounded page (Owner/Admin). The projection is PII-safe (the
   * invited email is the only personal datum) and never the token hash (threats
   * T6/T7). Pass optional `limit`/`offset` to page.
   */
  listInvitations(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<PendingWorkspaceInvitationResponse>> {
    return this.http.send<PageResponse<PendingWorkspaceInvitationResponse>>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/invitations`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/invitations/accept` — redeem a scoped
   * invitation. The token is a bearer grant: the authenticated caller becomes the
   * member with the invited role. The plaintext token travels in the body, never
   * the URL (threat T7); an invalid/expired/revoked/foreign token is hidden as
   * `404`, an already-a-member is `409`. Returns the created membership.
   */
  acceptInvitation(
    workspaceId: Uuid,
    request: AcceptWorkspaceInvitationRequest,
  ): Promise<WorkspaceMemberResponse> {
    return this.http.send<WorkspaceMemberResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/invitations/accept`,
      body: request,
    });
  }

  /**
   * `DELETE /api/v1/workspaces/{workspaceId}/invitations/{invitationId}` — revoke a
   * pending invitation so its scoped token can never be redeemed (a `Pending` →
   * `Revoked` transition, not a delete; Owner/Admin). Only a pending invitation may
   * be revoked (already-accepted/revoked is `409`). Responds `204 No Content`.
   */
  revokeInvitation(
    workspaceId: Uuid,
    invitationId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/invitations/${encodeURIComponent(invitationId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/workspaces/{workspaceId}/members/{memberId}` — read a SINGLE
   * workspace member together with its per-member weak `ETag` (CORE-WSM-003), the
   * read-with-ETag counterpart of {@link listMembers}. The roster keeps its
   * no-per-item-ETag collection contract (CORE-DX-002/003), so this is how a vertical
   * obtains a member's optimistic-concurrency token BEFORE a role change: pass the
   * returned `etag` as {@link ConditionalWriteOptions.ifMatch} to {@link updateMemberRole}
   * to make that change a true before-the-write conditional write (a stale token is
   * refused with `412`, not just a raced `409`). The body is the same generic
   * {@link WorkspaceMemberResponse} the role change returns; the token rides on the
   * response `ETag` header (`data` carries no email or token). Like the roster, this
   * read discloses membership, so a caller who may not administer the workspace — and a
   * foreign/unknown workspace or member — is hidden as `404` (a `LiveCoreApiError`),
   * never `403`.
   */
  getMemberWithETag(
    workspaceId: Uuid,
    memberId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SdkResponse<WorkspaceMemberResponse>> {
    return this.http.sendWithETag<WorkspaceMemberResponse>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/members/${encodeURIComponent(memberId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `PATCH /api/v1/workspaces/{workspaceId}/members/{memberId}` — change a member's
   * generic role (Owner/Admin), so an administrator can correct a role without
   * remove-and-reinvite. The last remaining Owner cannot be DEMOTED (a `409`). Pass
   * {@link ConditionalWriteOptions.ifMatch} — typically the `etag` from
   * {@link getMemberWithETag} — to make the change conditional on the version last read
   * (a stale value is refused with `412` BEFORE the write); omit it to change
   * unconditionally. Audited. Returns the updated membership; its new version rides on
   * the response `ETag` header (CORE-DX-002). A cross-tenant/unknown workspace or
   * member is hidden as `404`, a non-administration caller is `403`.
   */
  updateMemberRole(
    workspaceId: Uuid,
    memberId: Uuid,
    request: UpdateWorkspaceMemberRoleRequest,
    options?: ConditionalWriteOptions,
  ): Promise<WorkspaceMemberResponse> {
    return this.http.send<WorkspaceMemberResponse>({
      method: "PATCH",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/members/${encodeURIComponent(memberId)}`,
      body: request,
      ifMatch: options?.ifMatch,
    });
  }

  /**
   * `DELETE /api/v1/workspaces/{workspaceId}/members/{memberId}` — remove a
   * workspace member, revoking their access (Owner/Admin). The last Owner cannot be
   * removed. Audited. Responds `204 No Content`.
   */
  removeMember(
    workspaceId: Uuid,
    memberId: Uuid,
    params: { organizationSlug: string },
  ): Promise<void> {
    return this.http.send<void>({
      method: "DELETE",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/members/${encodeURIComponent(memberId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
