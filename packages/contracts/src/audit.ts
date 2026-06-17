// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Audit module contracts (CORE-SDK-006): the safe, generic read view of the
 * append-only audit log returned by `GET /api/v1/audit-logs` to an authorized
 * reader (Owner/Admin/Auditor). Every field is an identifier, a generic action
 * name or a generic state name — never a display name, an email, a token or any
 * resolved scene/content body, so the read can never leak content the audit log
 * itself does not store (threat T7).
 */

/** Safe, generic read view of one audit log entry. */
export interface AuditLogEntryView {
  /** Surrogate id of the audit entry (time-ordered). */
  id: Uuid;
  /** Tenant the audited action happened in, or `null` for a platform-level action. */
  organizationId: Uuid | null;
  /** Workspace the action happened in, or `null` for an organization-level action. */
  workspaceId: Uuid | null;
  /**
   * The kind of security-relevant action recorded, as a stable generic action
   * name (e.g. `SessionStarted`, `MemberRemoved`, `VisibilityRuleChanged`).
   */
  action: string;
  /** The user that performed the action, or `null` for a system action. */
  actorUserProfileId: Uuid | null;
  /** The governed resource kind name, or `null` when the action concerned no resource. */
  resourceType: string | null;
  /** The governed resource id, paired with {@link resourceType} or both `null`. */
  resourceId: Uuid | null;
  /** The selected participant of a private action, or `null` for an audience-wide action. */
  targetParticipantId: Uuid | null;
  /** The generic state name before the action, or `null`. */
  previousState: string | null;
  /** The generic state name after the action, or `null` when not a transition. */
  newState: string | null;
  /** When the audited action happened (UTC). */
  createdAt: IsoDateTimeString;
}

/**
 * One tenant-scoped, paged page of the append-only audit log. The log grows
 * without bound, so it is never returned whole: {@link entries} is at most
 * {@link limit} entries starting at {@link offset}, in chronological (append)
 * order, and {@link hasMore} tells the client whether a further page exists
 * (request the next page at `offset = offset + entries.length`).
 */
export interface AuditLogPageResponse {
  /** The single tenant this page of audit entries belongs to. */
  organizationId: Uuid;
  /** The zero-based index of the first entry in this page within the tenant's stream. */
  offset: number;
  /** The effective (server-clamped) maximum number of entries this page may contain. */
  limit: number;
  /** Whether at least one further entry exists after this page. */
  hasMore: boolean;
  /** The tenant-scoped, PII/secret-free audit entries, in chronological order. */
  entries: AuditLogEntryView[];
}
