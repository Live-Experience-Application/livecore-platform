// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Recaps module contracts (CORE-SDK-006): the role-projected read of a session's
 * recap returned by `GET /api/v1/sessions/{sessionId}/recap`. The recap body is
 * host content, so the read is projected by the caller's workspace role: the
 * host-content / metadata roles (Owner/Admin/Host/CoHost/Auditor) receive the full
 * {@link RecapView} WITH the {@link RecapView.summary} body, while the audience
 * roles (Participant/Observer) receive the host-only-field-stripped
 * {@link RecapSummaryView} WITHOUT the body — the recap body stays host content
 * until a separate reveal (docs/09_EVENT_CATALOG.md; threats T2/T8).
 */

/** Full, host/metadata-facing projection of a recap (the only view with the body). */
export interface RecapView {
  /** Surrogate id of the recap. */
  id: Uuid;
  /** Tenant the recap belongs to. */
  organizationId: Uuid;
  /** Workspace the recap belongs to. */
  workspaceId: Uuid;
  /** The session the recap summarizes. */
  sessionId: Uuid;
  /** The user that produced the recap, or `null` for a system/anonymized recap. */
  generatedByUserProfileId: Uuid | null;
  /** The recap format version. */
  recapVersion: number;
  /** The generic recap body (host content). */
  summary: string;
  /** When the recap was produced (UTC). */
  generatedAt: IsoDateTimeString;
}

/**
 * Audience-safe, host-only-field-stripped projection of a recap returned to the
 * audience roles. It deliberately omits the recap body, the producer, the
 * tenant/workspace boundary ids, the format version and the produced timestamp,
 * carrying only the non-sensitive id and the audience-relevant session id.
 */
export interface RecapSummaryView {
  /** Surrogate id of the recap; a non-sensitive handle. */
  id: Uuid;
  /** The session the recap belongs to; the audience-relevant identity. */
  sessionId: Uuid;
}
