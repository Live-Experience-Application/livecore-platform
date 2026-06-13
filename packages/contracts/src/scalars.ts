/**
 * Shared scalar wire aliases used across the Core API contracts (CORE-SDK-001).
 *
 * These are the JSON representations the Core API emits and accepts; a vertical
 * app consumes them as plain strings. They are deliberately simple aliases (not
 * branded types) so a contract value is exactly the value on the wire, with no
 * runtime wrapping required to call the API.
 */

/**
 * A surrogate identifier, serialized as a canonical UUID string (the Core mints
 * new ids as UUIDv7). Example: `"0190f1d4-9b6e-7c3a-8a1e-0c2b3d4e5f60"`.
 */
export type Uuid = string;

/**
 * A UTC instant serialized as an ISO 8601 / RFC 3339 date-time string. The Core
 * always emits server timestamps in UTC. Example:
 * `"2026-06-13T12:34:56.789+00:00"`.
 */
export type IsoDateTimeString = string;
