// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Visibility resource group (CORE-SDK-002): the idempotent reveal command and
 * the participant-visible feed.
 *
 * The reveal command is idempotent: a retry with the SAME `Idempotency-Key`
 * header produces no duplicate effect (docs/08_API_CONTRACTS.md "Idempotency").
 * The key is therefore a REQUIRED argument the caller controls — the SDK never
 * generates one per call, because a fresh key on every retry would defeat
 * idempotency. Reuse one key for one logical reveal across all its retries.
 */
import type {
  CreateVisibilityRuleRequest,
  HideRequest,
  HideResponse,
  PageResponse,
  ParticipantVisibleFeedResponse,
  RevealRequest,
  RevealResponse,
  Uuid,
  VisibilityRuleResponse,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import { pageQuery, type PageParams } from "./pagination.js";

/** Options for the reveal command. */
export interface RevealOptions {
  /**
   * The retry-safety key sent as the `Idempotency-Key` header. Reuse the SAME
   * value for every retry of one logical reveal.
   */
  idempotencyKey: string;
}

/** Options for the hide command. */
export interface HideOptions {
  /**
   * The retry-safety key sent as the `Idempotency-Key` header. Reuse the SAME
   * value for every retry of one logical hide.
   */
  idempotencyKey: string;
}

export class VisibilityClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `POST /api/v1/sessions/{sessionId}/reveal` — make a resource visible to the
   * audience (or to one selected participant), idempotently. The organization
   * slug travels in the request body; the retry-safety key is a header.
   */
  reveal(
    sessionId: Uuid,
    request: RevealRequest,
    options: RevealOptions,
  ): Promise<RevealResponse> {
    return this.http.send<RevealResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/reveal`,
      body: request,
      idempotencyKey: options.idempotencyKey,
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/hide` — hide / un-reveal a resource from the
   * audience (or from one selected participant), idempotently; emits `ContentHidden`.
   * It mirrors {@link reveal} with the opposite visibility sense: the organization
   * slug travels in the request body and the retry-safety key is a required header,
   * because a fresh key on every retry would defeat idempotency. Reuse one key for
   * one logical hide across all its retries.
   */
  hide(
    sessionId: Uuid,
    request: HideRequest,
    options: HideOptions,
  ): Promise<HideResponse> {
    return this.http.send<HideResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/hide`,
      body: request,
      idempotencyKey: options.idempotencyKey,
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/visibility-rules` — create a session-scoped
   * visibility rule for a resource (audience-wide, or for one selected
   * participant). The organization slug travels in the request body. The
   * same-workspace invariant for the referenced resource is enforced server-side;
   * the authoring caller always receives the host {@link VisibilityRuleResponse}.
   */
  createRule(
    sessionId: Uuid,
    request: CreateVisibilityRuleRequest,
  ): Promise<VisibilityRuleResponse> {
    return this.http.send<VisibilityRuleResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/visibility-rules`,
      body: request,
    });
  }

  /**
   * `GET /api/v1/sessions/{sessionId}/visibility-rules` — the session's visibility
   * rules (both the audience-wide and the selected-participant dimensions) in
   * deterministic id order, as a bounded page (CORE-DX-003). Restricted to the
   * authoring roles — a participant cannot enumerate rules.
   */
  listRules(
    sessionId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<VisibilityRuleResponse>> {
    return this.http.send<PageResponse<VisibilityRuleResponse>>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/visibility-rules`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `GET /api/v1/sessions/{sessionId}/visibility-rules/{ruleId}` — one visibility
   * rule within its session. A foreign-tenant, wrong-session, unknown rule or
   * non-member is hidden as `404`.
   */
  getRule(
    sessionId: Uuid,
    ruleId: Uuid,
    params: { organizationSlug: string },
  ): Promise<VisibilityRuleResponse> {
    return this.http.send<VisibilityRuleResponse>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/visibility-rules/${encodeURIComponent(ruleId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/participants/{participantId}/visible-feed` — a single
   * participant's private, already-filtered visible feed WITHIN a session. A
   * reveal is session-scoped (CORE-SVIS-001), so the feed is "what this
   * participant may see in this session"; the `sessionId` is required and a
   * reveal made in a concurrent session of the same workspace is never in it.
   */
  getParticipantVisibleFeed(
    participantId: Uuid,
    params: { organizationSlug: string; sessionId: Uuid },
  ): Promise<ParticipantVisibleFeedResponse> {
    return this.http.send<ParticipantVisibleFeedResponse>({
      method: "GET",
      path: `/participants/${encodeURIComponent(participantId)}/visible-feed`,
      query: {
        organizationSlug: params.organizationSlug,
        sessionId: params.sessionId,
      },
    });
  }
}
