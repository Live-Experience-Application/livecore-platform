// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
  MembershipRole,
  WorkspaceInvitationStatus,
  WorkspaceStatus,
} from "./enums.js";
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
  /**
   * Lifecycle status of the workspace (`Active` or `Archived`). An archived
   * workspace is read-only and excluded from the active workspace list
   * (CORE-LIFE-009).
   */
  status: WorkspaceStatus;
  /** When the workspace was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the workspace was last updated (UTC). */
  updatedAt: IsoDateTimeString;
  /**
   * The resource's optimistic-concurrency version — the opaque value of the weak
   * `ETag` the same single-resource response carries in its header (CORE-DX-002).
   * Echo it back as `If-Match` on a later rename/archive to make that write
   * conditional, so a GET-then-PUT across HTTP cannot silently clobber a concurrent
   * change. It is `null` on a representation with no single token to surface — a
   * list item, or a deployment whose provider maps no row version.
   */
  version: string | null;
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

/**
 * PII-safe response projection of a PENDING workspace invitation, returned by
 * `GET /api/v1/workspaces/{workspaceId}/invitations` (CORE-SDK-006). The
 * {@link invitedEmail} is the only personal datum; the token hash is never
 * projected and the one-time plaintext token is never returned on a read — only the
 * creation response carries a token, exactly once (threats T6/T7).
 */
export interface PendingWorkspaceInvitationResponse {
  /** Surrogate id of the invitation. */
  id: Uuid;
  /** Tenant the invitation belongs to. */
  organizationId: Uuid;
  /** Workspace the invite grants admission to. */
  workspaceId: Uuid;
  /** Email of the invitee (the only personal datum; data, not a credential). */
  invitedEmail: string;
  /** Generic role the invite will grant on redemption. */
  role: MembershipRole;
  /** Lifecycle status of the invitation (always `Pending` for this list). */
  status: WorkspaceInvitationStatus;
  /** When the scoped token expires (UTC). */
  expiresAt: IsoDateTimeString;
  /** When the invitation was created (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * PII-safe response projection of one of the CALLER'S OWN pending workspace
 * invitations, returned by the user-scoped invitation self-discovery read
 * `GET /api/v1/me/invitations` (CORE-INV-002). It is the audience-safe answer to
 * "which workspaces have invited ME?", so an onboarding flow can discover then accept
 * an invitation without the host handing over a workspace id out of band and without
 * enumerating workspaces.
 *
 * It is the user-scoped sibling of {@link PendingWorkspaceInvitationResponse} (the
 * host-facing manage-members list): it adds the organization {@link organizationSlug}
 * (which, with {@link workspaceId}, is exactly what an onboarding flow echoes back to
 * drive `acceptInvitation`) and it carries NO invited email or any other personal
 * datum — the only person it concerns is the caller, who already knows their own
 * email. The token hash is never projected and the one-time plaintext token is never
 * returned on a read (threats T6/T7).
 */
export interface MyPendingWorkspaceInvitationResponse {
  /** Surrogate id of the invitation. */
  id: Uuid;
  /** Tenant the invitation belongs to. */
  organizationId: Uuid;
  /** Canonical slug of the tenant; echo it as the accept request's `organizationSlug`. */
  organizationSlug: string;
  /** Workspace the invite grants admission to; the accept route's workspace id. */
  workspaceId: Uuid;
  /** Generic role the invite will grant on redemption. */
  role: MembershipRole;
  /** Lifecycle status of the invitation (always `Pending` for this list). */
  status: WorkspaceInvitationStatus;
  /** When the scoped token expires (UTC). */
  expiresAt: IsoDateTimeString;
  /** When the invitation was created (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * Request body for `POST /api/v1/workspaces/{workspaceId}/invitations/accept`
 * (CORE-SDK-006). The scoped invite token is a BEARER grant: the authenticated
 * caller becomes the member with the invited role. The plaintext token is carried
 * in the BODY (never the URL); the server stores and matches only its hash
 * (threats T6/T7).
 */
export interface AcceptWorkspaceInvitationRequest {
  /** Canonical slug of the organization that owns the target workspace. */
  organizationSlug: string;
  /** The one-time plaintext scoped invite token (carried in the body only). */
  token: string;
}

/**
 * Response projection of a workspace membership, returned when an invitation is
 * redeemed (CORE-SDK-006). Generic and product-neutral: identifiers, the granted
 * generic role and server timestamps only. It carries no invited email, no token
 * and no internal authorization rationale (data minimization; threats T6/T7).
 */
export interface WorkspaceMemberResponse {
  /** Surrogate id of the membership. */
  id: Uuid;
  /** Tenant the membership belongs to. */
  organizationId: Uuid;
  /** Workspace the membership grants standing in. */
  workspaceId: Uuid;
  /** Subject (the caller who redeemed the token) the membership is for. */
  userProfileId: Uuid;
  /** Generic role the membership grants. */
  role: MembershipRole;
  /** When the membership was created (UTC). */
  createdAt: IsoDateTimeString;
  /** When the membership was last updated (UTC). */
  updatedAt: IsoDateTimeString;
}
