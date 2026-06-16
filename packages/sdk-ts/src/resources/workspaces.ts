/**
 * Workspaces resource group (CORE-SDK-002): the tenant-scoped workspace
 * create/read/update and member-invite routes (csv/api_routes.csv). The server
 * authorizes every call; a caller who may not see the tenant or workspace is
 * hidden as `404` (a `LiveCoreApiError`), never `403`
 * (docs/06_AUTHORIZATION_MATRIX.md).
 */
import type {
  CreateWorkspaceRequest,
  InviteWorkspaceMemberRequest,
  UpdateWorkspaceRequest,
  Uuid,
  WorkspaceInvitationResponse,
  WorkspaceResponse,
} from "@livecore/contracts";

import type { HttpClient, SdkResponse } from "../http.js";

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

  /** `GET /api/v1/workspaces` — workspaces the caller is a member of. */
  list(params: { organizationSlug: string }): Promise<WorkspaceResponse[]> {
    return this.http.send<WorkspaceResponse[]>({
      method: "GET",
      path: "/workspaces",
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /** `POST /api/v1/workspaces` — create a workspace (organization Owner/Admin). */
  create(request: CreateWorkspaceRequest): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "POST",
      path: "/workspaces",
      body: request,
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
}
