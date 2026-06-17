import type { IsoDateTimeString, Uuid } from "./scalars.js";

/**
 * Realtime module contracts (CORE-SDK-001): the reconnect-replay projection of
 * the durable session event stream returned by
 * `GET /api/v1/sessions/{sessionId}/events`. Each item is the SAME recipient-safe
 * shape the live hub delivers, so a client processes a replayed event and a live
 * event through one code path (docs/09_EVENT_CATALOG.md "Reconnect replay";
 * docs/11_REALTIME_SYNC.md).
 */

/**
 * A single recipient-safe replay item. {@link targetParticipantId} is populated
 * only on the host projection (the "to whom" routing confirmation); the audience
 * projection leaves it `null`, so an audience recipient never learns who else was
 * targeted (threats T2/T7).
 */
export interface SessionEventReplayItem {
  /** Surrogate id of the event. */
  eventId: Uuid;
  /**
   * The per-session, gap-free, strictly monotonic sequence number (CORE-RTC-001).
   * The client orders the stream by it, acknowledges replay by it (the cursor is
   * `afterSequence`), and detects a missed event as a gap in the sequence.
   */
  sequence: number;
  /**
   * The generic, product-neutral event type name (docs/09_EVENT_CATALOG.md). A
   * plain string for forward compatibility; the names known today are exported as
   * {@link KnownSessionEventTypes}.
   */
  eventType: string;
  /** The session the event belongs to. */
  sessionId: Uuid;
  /**
   * The server-composed payload — resource identifiers only, never content
   * (threat T7) — as a raw JSON string. For a known {@link KnownSessionEventType}
   * a consumer parses it into the typed {@link SessionEventPayloadMap} shape and
   * discriminates by {@link eventType} via {@link ParsedSessionEvent}; an unknown
   * future event keeps its payload as an opaque string.
   */
  payload: string;
  /** The payload schema version (docs/09_EVENT_CATALOG.md). */
  schemaVersion: number;
  /** When the event happened (UTC). */
  createdAt: IsoDateTimeString;
  /**
   * The routing target on the host projection, or `null` on the audience
   * projection and for an audience-wide event.
   */
  targetParticipantId: Uuid | null;
}

/** Recipient-safe response of the reconnect-replay route. */
export interface SessionEventReplayResponse {
  /** The session whose stream was replayed. */
  sessionId: Uuid;
  /**
   * The recipient-safe events the caller is entitled to, in append (sequence)
   * order — only those after the acknowledged sequence cursor when one was
   * supplied.
   */
  events: SessionEventReplayItem[];
  /** Server timestamp (UTC) at which the replay was computed. */
  generatedAt: IsoDateTimeString;
}

/**
 * Live realtime hub contract (CORE-RT-007): the stable mirror of the server's
 * SignalR live path so a vertical can open the live session stream without
 * hard-coding the server's C# constants. The live stream carries the SAME
 * recipient-safe envelopes reconnect replay returns, so a client processes a live
 * event and a replayed event through one handler (docs/11_REALTIME_SYNC.md).
 *
 * The hub authenticates with the same OIDC bearer token the REST API uses, and a
 * client supplies only IDENTIFIERS to connect — never group names — so it can never
 * choose a server-managed group or subscribe to another participant's feed; the
 * server resolves the authorized groups from those identifiers (CORE-RT-002; threat
 * T3 in docs/07_SECURITY_THREAT_MODEL.md). These constants/types are the typed
 * client's source of truth for the path, the client-method name and the connection
 * parameters; the live SDK client that drives them is `@livecore/sdk-ts`.
 */

/**
 * The server-owned SignalR hub paths, fixed by the server — a client never chooses
 * a path (mirror of `apps/api/Realtime/RealtimeHubRoutes.cs` and the `/hubs` prefix
 * in `apps/api/IdentityAccess/HubBearerToken.cs`). Each path is mounted at the API
 * origin, NOT under {@link API_BASE_PATH}: the `/hubs` area is where the bearer
 * handler's query-string-token rule applies and the REST version prefix does not.
 */
export const RealtimeHubPaths = {
  /** The single session realtime hub: `/hubs/session`. */
  session: "/hubs/session",
} as const;

/** A server-owned realtime hub path. */
export type RealtimeHubPath =
  (typeof RealtimeHubPaths)[keyof typeof RealtimeHubPaths];

/**
 * The single SignalR client method the server invokes on a recipient connection to
 * deliver a session event (mirror of `SessionEventEnvelope.ClientMethod`). A client
 * subscribes to exactly this method to receive the live stream; the delivered
 * argument is a {@link LiveSessionEvent} — the same {@link SessionEventReplayItem}
 * shape reconnect replay returns.
 */
export const SESSION_EVENT_CLIENT_METHOD = "SessionEvent";

/**
 * The query-string parameter a hub connection carries its OIDC bearer token on
 * (mirror of `HubBearerToken.QueryParameter`). Browser WebSocket clients cannot set
 * the `Authorization` header, so the SignalR client sends the token here; the server
 * accepts a query-string token ONLY for hub paths (under `/hubs`), never for the REST
 * API, so a token is never read from a non-hub URL (threat T7).
 */
export const REALTIME_ACCESS_TOKEN_QUERY_PARAM = "access_token";

/**
 * The connection-parameter shape a client supplies to open a live session hub
 * connection — the mirror of the query keys the server reads in
 * `SessionHub.OnConnectedAsync`. A client supplies only IDENTIFIERS, so it can never
 * choose a group or subscribe to another participant's feed; the server resolves the
 * authorized server-managed groups from these identifiers (CORE-RT-002). The same
 * identifiers the reconnect-replay route takes, so live and replay address the same
 * session and participant feed the same way.
 */
export interface SessionHubConnectionParams {
  /** Canonical slug of the organization that owns the session's workspace. */
  organizationSlug: string;
  /** The session to connect to. */
  sessionId: Uuid;
  /**
   * The caller's own participant id, when connecting as a participant. A caller can
   * only ever connect to a participant feed they own — the server aborts any other,
   * indistinguishably (threats T1/T5) — exactly like the reconnect-replay
   * `participantId`.
   */
  participantId?: Uuid;
}

/**
 * A single live session event delivered over the realtime hub. It is the SAME
 * recipient-safe shape reconnect replay returns ({@link SessionEventReplayItem}), so
 * a client routes a live event and a replayed event through one handler — the live
 * path and the reconnect-replay path never diverge (docs/11_REALTIME_SYNC.md).
 */
export type LiveSessionEvent = SessionEventReplayItem;
