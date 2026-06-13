/**
 * The Core session event vocabulary (CORE-SDK-001; docs/09_EVENT_CATALOG.md).
 *
 * Session events are append-only, drive live-state reconstruction, and are
 * delivered live over the realtime hub and replayed on reconnect as
 * recipient-safe envelopes (see {@link SessionEventReplayItem}). A Core event
 * type is a stable, product-neutral string name.
 *
 * The catalog is intentionally EXTENSIBLE: later Core stories add names as their
 * commands begin emitting events. The replay item therefore types `eventType` as
 * a plain `string` for forward compatibility, and the names a consumer can switch
 * on TODAY are exported here as a runtime tuple so handling known events stays
 * typed without rejecting a future event a newer server emits.
 */

/** Session event type names Core emits today (docs/09_EVENT_CATALOG.md). */
export const KnownSessionEventTypes = ["ContentRevealed"] as const;

/** A session event type name Core emits today. */
export type KnownSessionEventType = (typeof KnownSessionEventTypes)[number];
