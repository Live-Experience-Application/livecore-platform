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
  ParticipantVisibleFeedResponse,
  RevealRequest,
  RevealResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";

/** Options for the reveal command. */
export interface RevealOptions {
  /**
   * The retry-safety key sent as the `Idempotency-Key` header. Reuse the SAME
   * value for every retry of one logical reveal.
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
