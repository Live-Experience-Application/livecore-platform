import type { MembershipRole, WorkspaceInvitationStatus } from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Workspaces module contracts (CORE-SDK-001). Generic, product-neutral DTOs for
 * the workspace create/read/update and member-invite routes
 * (csv/api_routes.csv); they carry only generic Core fields and no internal
 * authorization rationale (docs/08_API_CONTRACTS.md).
 */

/** Request body for `POST /api/v1/workspaces`. */
export interface CreateWorkspaceRequest {
  /** Canonical slug of the target organization the workspace is created in. */
  organizationSlug: string;
  /** Per-tenant natural key of the new workspace (lower-case, URL-safe). */
  slug: string;
  /** Human-readable display name of the workspace. */
  name: string;
}

/** Request body for `PUT /api/v1/workspaces/{workspaceId}` (rename only). */
export interface UpdateWorkspaceRequest {
  /** Canonical slug of the organization that owns the workspace. */
  organizationSlug: string;
  /** New human-readable display name of the workspace. */
  name: string;
}

/** Response projection of a workspace. */
export interface WorkspaceResponse {
  /** Surrogate id of the workspace. */
  id: Uuid;
  /** Tenant the workspace belongs to. */
  organizationId: Uuid;
  /** Per-tenant natural key of the workspace. */
  slug: string;
  /** Human-readable display name. */
  name: string;
  /** When the workspace was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the workspace was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}

/** Request body for `POST /api/v1/workspaces/{workspaceId}/members`. */
export interface InviteWorkspaceMemberRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /** Email of the invitee (informational data only, never a credential). */
  email: string;
  /** Generic role the invite will grant on redemption. */
  role: MembershipRole;
}

/**
 * Response of creating a workspace invitation. The one-time plaintext
 * {@link token} is returned exactly once here and never again; the server stores
 * only its hash.
 */
export interface WorkspaceInvitationResponse {
  /** Surrogate id of the invitation. */
  id: Uuid;
  /** Tenant the invitation belongs to. */
  organizationId: Uuid;
  /** Workspace the invite grants admission to. */
  workspaceId: Uuid;
  /** Generic role the invite will grant on redemption. */
  role: MembershipRole;
  /** Lifecycle status of the invitation (`Pending` at creation). */
  status: WorkspaceInvitationStatus;
  /** When the scoped token expires (UTC). */
  expiresAt: IsoDateTimeString;
  /** When the invitation was created (UTC). */
  createdAt: IsoDateTimeString;
  /** The one-time plaintext scoped token. Returned exactly once. */
  token: string;
}
