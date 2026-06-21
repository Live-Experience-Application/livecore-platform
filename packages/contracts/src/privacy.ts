// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type {
  MembershipRole,
  ParticipantStatus,
  WorkspaceInvitationStatus,
} from "./enums.js";
import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Privacy & data-protection contracts (CORE-SDK-006): the machine-readable
 * data-subject access/portability export returned by
 * `GET /api/v1/organizations/{organizationSlug}/members/{memberId}/personal-data-export`
 * (GDPR Art.15 access / Art.20 portability). Unlike the principal-context or audit
 * views, this projection DELIBERATELY carries the subject's personal data — that is
 * the entire point of an access/portability export — and is delivered ONLY to the
 * entitled subject or an Owner/Admin acting on their behalf. What it never carries
 * is a SECRET: no invitation token or token hash, no access token, no credential
 * (threats T6/T7). Every record is the subject's OWN data, scoped to the resolved
 * tenant.
 */

/** The subject's identity profile within a personal-data export. */
export interface PersonalDataExportSubjectResponse {
  /** Surrogate id of the subject's user profile. */
  id: Uuid;
  /** Identity of the OIDC provider that asserted the subject. */
  issuer: string;
  /** Issuer-local stable identifier of the subject. */
  subject: string;
  /** Optional human-readable name mirrored from the provider. */
  displayName: string | null;
  /** Optional email address mirrored from the provider. */
  email: string | null;
}

/** The subject's organization membership within a personal-data export. */
export interface PersonalDataExportOrganizationMembershipResponse {
  /** Surrogate id of the membership row. */
  id: Uuid;
  /** The tenant the membership grants standing in. */
  organizationId: Uuid;
  /** The generic role the subject holds in the organization. */
  role: MembershipRole;
  /** When the membership was created (UTC). */
  createdAt: IsoDateTimeString;
}

/** The subject's workspace membership within a personal-data export. */
export interface PersonalDataExportWorkspaceMembershipResponse {
  /** Surrogate id of the membership row. */
  id: Uuid;
  /** The workspace the membership grants standing in. */
  workspaceId: Uuid;
  /** The generic role the subject holds in the workspace. */
  role: MembershipRole;
  /** When the membership was created (UTC). */
  createdAt: IsoDateTimeString;
}

/** The subject's participant record within a personal-data export. */
export interface PersonalDataExportParticipantResponse {
  /** Surrogate id of the participant row. */
  id: Uuid;
  /** The workspace the participant belongs to. */
  workspaceId: Uuid;
  /** The participant's display identity (the subject's personal datum on this record). */
  displayName: string;
  /** The participant's lifecycle status. */
  status: ParticipantStatus;
  /** When the participant was created (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * An invitation addressed to the subject within a personal-data export. The invite
 * token and its hash are NEVER projected — the only secret on the aggregate stays
 * out of the export (threats T6/T7).
 */
export interface PersonalDataExportInvitationResponse {
  /** Surrogate id of the invitation row. */
  id: Uuid;
  /** The workspace the invitation scoped. */
  workspaceId: Uuid;
  /** The invited email address (the subject's personal datum). */
  invitedEmail: string;
  /** The generic role the invitation offered. */
  role: MembershipRole;
  /** The invitation's lifecycle status. */
  status: WorkspaceInvitationStatus;
  /** When the invitation expires (UTC). */
  expiresAt: IsoDateTimeString;
  /** When the invitation was created (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * A Web Push subscription registered by the subject within a personal-data export
 * (CORE-PUSH-001). The encryption keys (`p256dh` and the `auth` secret) are NEVER
 * projected — the subscription's secret stays out of the export, exactly as the
 * invitation token hash does (threats T6/T7).
 */
export interface PersonalDataExportPushSubscriptionResponse {
  /** Surrogate id of the subscription row. */
  id: Uuid;
  /** The push service endpoint URL the subscription is registered against. */
  endpoint: string;
  /** When the subscription was registered (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * Machine-readable response of a data-subject access/portability export: the
 * personal data Core holds about ONE data subject within ONE tenant.
 */
export interface PersonalDataExportResponse {
  /** The tenant the export was produced within (the scope of every collection below). */
  organizationId: Uuid;
  /** The subject's identity profile (the disclosed identity datum). */
  subject: PersonalDataExportSubjectResponse;
  /** The subject's membership in this tenant, or `null` if none. */
  organizationMembership: PersonalDataExportOrganizationMembershipResponse | null;
  /** The subject's workspace memberships within this tenant. */
  workspaceMemberships: PersonalDataExportWorkspaceMembershipResponse[];
  /** The subject's participant records within this tenant. */
  participants: PersonalDataExportParticipantResponse[];
  /** The invitations addressed to the subject's email within this tenant. */
  invitations: PersonalDataExportInvitationResponse[];
  /** The subject's global Web Push subscriptions (per-principal personal data). */
  pushSubscriptions: PersonalDataExportPushSubscriptionResponse[];
}
