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

import type { HttpClient } from "../http.js";

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

  /** `PUT /api/v1/workspaces/{workspaceId}` — rename a workspace. */
  update(
    workspaceId: Uuid,
    request: UpdateWorkspaceRequest,
  ): Promise<WorkspaceResponse> {
    return this.http.send<WorkspaceResponse>({
      method: "PUT",
      path: `/workspaces/${encodeURIComponent(workspaceId)}`,
      body: request,
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
