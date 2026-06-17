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
