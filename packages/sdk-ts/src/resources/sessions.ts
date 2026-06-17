/**
 * Sessions resource group (CORE-SDK-002, extended in CORE-SDK-006): the workspace
 * session list/create, the by-session-id read, the lifecycle commands
 * (`Prepared` → `Live` → `Ended`, plus `cancel` for a not-yet-started session) and
 * the host-driven participant join/leave presence commands. The target
 * organization is the required `organizationSlug` query parameter (or, for create,
 * a body field); `start` requires the session to be `Prepared`, `end` requires it
 * to be `Live`, and `cancel` requires it to be `Prepared`, so any other state
 * surfaces as a `409` `LiveCoreApiError` (csv/api_routes.csv).
 */
import type {
  CreateSessionRequest,
  PageResponse,
  ParticipantPresenceResponse,
  SessionResponse,
  Uuid,
} from "@livecore/contracts";

import type { HttpClient } from "../http.js";
import { pageQuery, type PageParams } from "./pagination.js";

export class SessionsClient {
  constructor(private readonly http: HttpClient) {}

  /**
   * `GET /api/v1/workspaces/{workspaceId}/sessions` — the workspace's sessions, as
   * a bounded page (CORE-DX-003). Pass optional `limit`/`offset` to page; the
   * result is the `items + hasMore` envelope, never an unbounded array.
   */
  list(
    workspaceId: Uuid,
    params: { organizationSlug: string } & PageParams,
  ): Promise<PageResponse<SessionResponse>> {
    return this.http.send<PageResponse<SessionResponse>>({
      method: "GET",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/sessions`,
      query: {
        organizationSlug: params.organizationSlug,
        ...pageQuery(params),
      },
    });
  }

  /**
   * `POST /api/v1/workspaces/{workspaceId}/sessions` — create a session. It is
   * always created `Prepared`; the only way into the live timeline is the guarded
   * {@link start} command.
   */
  create(
    workspaceId: Uuid,
    request: CreateSessionRequest,
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/workspaces/${encodeURIComponent(workspaceId)}/sessions`,
      body: request,
    });
  }

  /**
   * `GET /api/v1/sessions/{sessionId}` — read one session within its workspace. A
   * single generic role-projected `SessionResponse`; a foreign-tenant, unknown
   * session or non-member is hidden as `404`.
   */
  get(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "GET",
      path: `/sessions/${encodeURIComponent(sessionId)}`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /** `POST /api/v1/sessions/{sessionId}/start` — begin the live timeline. */
  start(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/start`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /** `POST /api/v1/sessions/{sessionId}/end` — end the live timeline. */
  end(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/end`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/cancel` — cancel a not-yet-started
   * (`Prepared`) session (CORE-LIFE-010). A soft, terminal `Prepared` → `Cancelled`
   * transition (never a delete), so the session's append-only history is preserved;
   * a session that is not `Prepared` surfaces as a `409` `LiveCoreApiError`.
   */
  cancel(
    sessionId: Uuid,
    params: { organizationSlug: string },
  ): Promise<SessionResponse> {
    return this.http.send<SessionResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/cancel`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/participants/{participantId}/join` — admit a
   * participant (host-driven). Emits the identifier-only `ParticipantJoined` event
   * and enforces the `session.participant.max` quota; a removed participant, an
   * ended session or an over-quota join is `409`. The response is identifier-only —
   * never a participant display name or any PII (threat T7).
   */
  joinParticipant(
    sessionId: Uuid,
    participantId: Uuid,
    params: { organizationSlug: string },
  ): Promise<ParticipantPresenceResponse> {
    return this.http.send<ParticipantPresenceResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/participants/${encodeURIComponent(participantId)}/join`,
      query: { organizationSlug: params.organizationSlug },
    });
  }

  /**
   * `POST /api/v1/sessions/{sessionId}/participants/{participantId}/leave` — remove
   * a participant (host-driven). On an actual departure it emits the
   * identifier-only `ParticipantLeft` event, releases the participant-quota slot and
   * evicts the realtime connection; a participant that had already left is an
   * idempotent no-op (`outcome: "AlreadyLeft"`).
   */
  leaveParticipant(
    sessionId: Uuid,
    participantId: Uuid,
    params: { organizationSlug: string },
  ): Promise<ParticipantPresenceResponse> {
    return this.http.send<ParticipantPresenceResponse>({
      method: "POST",
      path: `/sessions/${encodeURIComponent(sessionId)}/participants/${encodeURIComponent(participantId)}/leave`,
      query: { organizationSlug: params.organizationSlug },
    });
  }
}
