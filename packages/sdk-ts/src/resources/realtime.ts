// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Realtime resource group (CORE-SDK-002, CORE-RT-007): the live SignalR session
 * stream AND the REST reconnect replay of the same durable event stream. Both
 * deliver the SAME recipient-safe {@link LiveSessionEvent} /
 * {@link SessionEventReplayItem} shape, so a client routes a live event and a
 * replayed event through ONE handler — the live path and the reconnect-replay path
 * never diverge (docs/11_REALTIME_SYNC.md).
 *
 * Live ({@link connect}): opens the `/hubs/session` hub and joins only the
 * server-managed groups the server resolves from the connection IDENTIFIERS — the
 * client never names a group, so it can never select a group or another
 * participant's feed (CORE-RT-002; threat T3). The connection fails closed without an
 * access token, exactly like the REST transport.
 *
 * Replay ({@link getSessionEvents}): rebuilds live state after a disconnect; the
 * server re-applies the same per-recipient filter, so a hidden event is never
 * replayed (docs/09_EVENT_CATALOG.md "Reconnect replay"; threat T3). A participant
 * replaying its own feed identifies itself with `participantId`, exactly like the
 * live hub connection.
 */
import {
  RealtimeHubPaths,
  SESSION_EVENT_CLIENT_METHOD,
} from "@livecore/contracts";
import type {
  LiveSessionEvent,
  ParticipantRosterView,
  SessionEventReplayResponse,
  SessionHubConnectionParams,
  SessionParticipantContext,
  SessionRosterView,
  Uuid,
} from "@livecore/contracts";

import { LiveCoreError } from "../errors.js";
import type { HttpClient } from "../http.js";
import type {
  HubConnectionFactory,
  LiveSessionConnection,
} from "../options.js";

/** Query parameters for the reconnect-replay route. */
export interface SessionEventReplayParams {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /**
   * The caller's own participant id, when replaying a participant feed. A caller
   * can only ever replay a participant feed they own (the server hides any other
   * as `404`).
   */
  participantId?: Uuid;
  /**
   * The caller's last acknowledged per-session sequence number (CORE-RTC-001);
   * events with a greater sequence are replayed, so a cursor of N returns N+1..
   * with no skips or duplicates. Omit to replay the whole stream (the client
   * deduplicates).
   */
  afterSequence?: number;
}

export class RealtimeClient {
  constructor(
    private readonly http: HttpClient,
    private readonly hubConnectionFactory?: HubConnectionFactory,
  ) {}

  /**
   * Opens a live SignalR connection to the session hub (`/hubs/session`) and
   * delivers each live session event to {@link onEvent} (CORE-RT-007).
   *
   * The connection supplies only the {@link SessionHubConnectionParams} IDENTIFIERS
   * on the hub URL — `organizationSlug`, `sessionId` and, for a participant, its own
   * `participantId` — never a group name, so the server resolves the authorized
   * server-managed groups and the client can never select a group or another
   * participant's feed (CORE-RT-002; threat T3). The single server-fixed
   * `SessionEvent` client method is the one handler, and every delivered envelope is
   * the same {@link LiveSessionEvent} shape {@link getSessionEvents} replays, so a
   * caller can route live and replayed events through one code path.
   *
   * Fails closed: it resolves the access token BEFORE building or starting any
   * connection, so an empty token rejects with a `LiveCoreError` and NO hub
   * connection is opened (the token then refreshes on each reconnect via the
   * connection's `accessTokenFactory`). It also rejects if no `hubConnectionFactory`
   * was configured on the client.
   *
   * @returns a handle whose {@link LiveSessionConnection.stop} closes the connection.
   */
  async connect(
    params: SessionHubConnectionParams,
    onEvent: (event: LiveSessionEvent) => void,
  ): Promise<LiveSessionConnection> {
    if (this.hubConnectionFactory === undefined) {
      throw new LiveCoreError(
        "No hubConnectionFactory was configured; pass one in LiveCoreClientOptions to open a live realtime connection.",
      );
    }

    // Fail closed BEFORE building or starting any connection: an empty token throws
    // here, so a hub connection is never opened unauthenticated (mirrors the REST
    // transport's fail-closed behavior).
    await this.http.resolveAccessToken();

    const connection = this.hubConnectionFactory({
      url: this.buildHubUrl(params),
      accessTokenFactory: () => this.http.resolveAccessToken(),
    });

    // One handler for the single server-fixed client method. The delivered envelope
    // is the recipient-safe SessionEventReplayItem shape; the consumer can feed the
    // same handler the items getSessionEvents replays on reconnect.
    connection.on(SESSION_EVENT_CLIENT_METHOD, (envelope) => {
      onEvent(envelope as LiveSessionEvent);
    });

    await connection.start();

    return { stop: () => connection.stop() };
  }

  /**
   * `GET /api/v1/sessions/{sessionId}/events` — the recipient-safe events the
   * caller is entitled to, in append (sequence) order.
   */
  getSessionEvents(
    sessionId: Uuid,
    params: SessionEventReplayParams,
  ): Promise<SessionEventReplayResponse> {
    return this.http.send<SessionEventReplayResponse>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/events`,
      query: {
        organizationSlug: params.organizationSlug,
        participantId: params.participantId,
        afterSequence: params.afterSequence?.toString(),
      },
    });
  }

  /**
   * `GET /api/v1/sessions/{sessionId}/roster` — the session's participant roster
   * (its workspace's active participants) with each participant's realtime presence
   * state, projected by the caller's workspace role (CORE-PRS-002).
   *
   * The host-content roles (Owner/Admin/Host/CoHost) receive the full
   * {@link SessionRosterView} WITH each participant's host-only user link; the
   * audience roles (Participant/Observer/Auditor) receive the host-only-field-stripped
   * {@link ParticipantRosterView} — so the return type is the union of both shapes. A
   * foreign tenant, an unknown session or a non-member of the session's workspace is
   * hidden as `404` (a `LiveCoreApiError`), fail-closed (threats T1/T5/T2). Presence
   * reflects active realtime connections (per API instance; docs/11_REALTIME_SYNC.md).
   */
  getSessionRoster(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionRosterView | ParticipantRosterView> {
    return this.http.send<SessionRosterView | ParticipantRosterView>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/roster`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `GET /api/v1/sessions/{sessionId}/me` — the CALLER'S OWN participant context
   * (its participant id, display name and presence) for the session (CORE-PSELF-001).
   *
   * It lets an audience surface learn its OWN surrogate participant id in-scope, so
   * it can then call the participant-keyed reads ({@link getSessionRoster}, the
   * visible feed) for itself — without the host passing the surrogate participant id
   * out of band. The caller's participant is resolved entirely server-side from the
   * authenticated principal (never a client-supplied id), so a caller can only ever
   * resolve ITSELF. A foreign tenant, an unknown session and a caller who is not a
   * participant of the session are hidden as `404` (a `LiveCoreApiError`),
   * fail-closed (threats T1/T5).
   */
  getSessionParticipantContext(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionParticipantContext> {
    return this.http.send<SessionParticipantContext>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}/me`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * Composes the live hub URL: the API origin, the server-owned hub path and the
   * connection IDENTIFIERS as query parameters (never a group name). The access
   * token is NOT placed here — it travels via the connection's `accessTokenFactory`
   * (the `access_token` query parameter) so the secret never sits in the URL the SDK
   * composes (threat T7).
   */
  private buildHubUrl(params: SessionHubConnectionParams): string {
    const pairs: string[] = [
      `organizationSlug=${encodeURIComponent(params.organizationSlug)}`,
      `sessionId=${encodeURIComponent(params.sessionId)}`,
    ];
    if (params.participantId !== undefined) {
      pairs.push(`participantId=${encodeURIComponent(params.participantId)}`);
    }
    return `${this.http.origin}${RealtimeHubPaths.session}?${pairs.join("&")}`;
  }
}
