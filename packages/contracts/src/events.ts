// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

import type { Uuid } from "./scalars.js";

/**
 * The Core session event vocabulary and its identifier-only payload contracts
 * (CORE-SDK-001, completed by CORE-RT-008; docs/09_EVENT_CATALOG.md).
 *
 * Session events are append-only, drive live-state reconstruction, and are
 * delivered live over the realtime hub and replayed on reconnect as
 * recipient-safe envelopes (see {@link SessionEventReplayItem}). A Core event
 * type is a stable, product-neutral string name, and every emitted event carries
 * a server-composed payload of resource IDENTIFIERS only — never resolved content
 * (threat T7).
 *
 * {@link KnownSessionEventTypes} is the set of names Core emits today: it equals
 * the non-deferred rows of `csv/event_catalog.csv` and the constants of the server
 * catalog `apps/api/Realtime/SessionEventTypes.cs`. A CI drift gate
 * (`pnpm --filter @livecore/contracts run check:events`, mirrored by the package
 * test) fails if this vocabulary or the payload field sets below diverge from
 * those server sources — the TypeScript-side mirror of the C# catalog-as-contract
 * binding (spec-consistency check 11; docs/23_PACKAGE_VERSIONING.md).
 *
 * The catalog stays forward-compatible: the wire {@link SessionEventReplayItem.eventType}
 * is a plain `string` and {@link SessionEventReplayItem.payload} a raw JSON string,
 * so a consumer never rejects a future event a newer server emits. The names a
 * consumer can switch on TODAY are this tuple, and the parsed payloads it can
 * discriminate by event type are {@link SessionEventPayloadMap} / {@link ParsedSessionEvent}.
 *
 * PAYLOAD FIELD CASING. The Core route DTOs are camelCase JSON, but a session
 * event's `payload` is composed by the server with the default System.Text.Json
 * options (no naming policy), so its fields are PascalCase. These contracts mirror
 * the server field names exactly (the names the drift gate parses from the C#
 * payload records), so the casing is deliberate, not an oversight.
 */

/** Session event type names Core emits today (docs/09_EVENT_CATALOG.md). */
export const KnownSessionEventTypes = [
  "SessionCreated",
  "SessionStarted",
  "SessionEnded",
  "ParticipantJoined",
  "ParticipantLeft",
  "SceneActivated",
  "VisibilityRuleChanged",
  "ContentRevealed",
  "ContentHidden",
  "RecapGenerated",
] as const;

/** A session event type name Core emits today. */
export type KnownSessionEventType = (typeof KnownSessionEventTypes)[number];

/**
 * The payload of a `SessionCreated`, `SessionStarted` or `SessionEnded` event:
 * the session id and its lifecycle status name (e.g. `Prepared`/`Live`/`Ended`).
 * Identifiers and a generic state name only — never any content (threat T7).
 */
export interface SessionLifecycleEventPayload {
  /** The session the lifecycle event concerns. */
  SessionId: Uuid;
  /** The session's lifecycle status name after the transition (a {@link SessionStatus}). */
  Status: string;
}

/**
 * The payload of a `ParticipantJoined` / `ParticipantLeft` presence event: the
 * surrogate id of the participant who joined or left. The participant id is an
 * opaque surrogate — never a display name or any other PII (threat T7).
 */
export interface ParticipantPresenceEventPayload {
  /** The participant who joined or left. */
  ParticipantId: Uuid;
}

/**
 * The payload of a `ContentRevealed` / `ContentHidden` event: the generic kind
 * and id of the revealed/hidden resource. Identifiers only — never the resolved
 * content (threat T7).
 */
export interface ResourceReferenceEventPayload {
  /** The generic kind of the resource (a {@link VisibilityResourceType}). */
  ResourceType: string;
  /** The id of the resource. */
  ResourceId: Uuid;
}

/**
 * The payload of a `VisibilityRuleChanged` event: the changed resource's generic
 * kind and id plus the new visibility state name (e.g. `Visible`/`Hidden`).
 * Identifiers and a state name only — never resolved content (threat T7).
 */
export interface VisibilityRuleChangedEventPayload {
  /** The generic kind of the resource whose rule changed. */
  ResourceType: string;
  /** The id of the resource whose rule changed. */
  ResourceId: Uuid;
  /** The new visibility state name (e.g. `Visible`/`Hidden`). */
  Visibility: string;
}

/**
 * The payload of a `SceneActivated` event: the id of the activated scene.
 * Identifier only — never resolved content (threat T7).
 */
export interface SceneActivatedEventPayload {
  /** The activated scene. */
  SceneId: Uuid;
}

/**
 * The payload of a `RecapGenerated` event: the recap id and the session it
 * summarizes. Identifiers only — never the recap body (threat T7).
 */
export interface RecapGeneratedEventPayload {
  /** The generated recap. */
  RecapId: Uuid;
  /** The session the recap summarizes. */
  SessionId: Uuid;
}

/**
 * The payload contract for each known session event type: the typed shape of the
 * JSON a consumer obtains by parsing {@link SessionEventReplayItem.payload} for a
 * given `eventType`. Every {@link KnownSessionEventType} has exactly one entry,
 * each an identifier-only shape matching the server's payload record
 * (`apps/api/Realtime/SessionEventPayloads.cs`); the drift gate keeps the two in
 * step.
 */
export interface SessionEventPayloadMap {
  SessionCreated: SessionLifecycleEventPayload;
  SessionStarted: SessionLifecycleEventPayload;
  SessionEnded: SessionLifecycleEventPayload;
  ParticipantJoined: ParticipantPresenceEventPayload;
  ParticipantLeft: ParticipantPresenceEventPayload;
  SceneActivated: SceneActivatedEventPayload;
  VisibilityRuleChanged: VisibilityRuleChangedEventPayload;
  ContentRevealed: ResourceReferenceEventPayload;
  ContentHidden: ResourceReferenceEventPayload;
  RecapGenerated: RecapGeneratedEventPayload;
}

/**
 * A parsed session event discriminated by its event type: the typed view a
 * consumer constructs from a {@link SessionEventReplayItem} (or a live hub event)
 * by parsing the raw `payload` string for a known `eventType`. Switching on
 * `eventType` narrows `payload` to the exact {@link SessionEventPayloadMap} shape,
 * so a vertical handles every known event with full type information instead of
 * blind-parsing an opaque string.
 */
export type ParsedSessionEvent = {
  [K in KnownSessionEventType]: {
    /** The discriminant: a known event type name. */
    eventType: K;
    /** The parsed, typed payload for this event type. */
    payload: SessionEventPayloadMap[K];
  };
}[KnownSessionEventType];

/**
 * The payload field names of each known session event, as a runtime map (the
 * type-erased mirror of {@link SessionEventPayloadMap}). The drift gate compares
 * this to the field names parsed from the server's payload records
 * (`apps/api/Realtime/SessionEventPayloads.cs`) and a type test binds each tuple
 * to the matching payload interface's keys, so the published payload field set
 * cannot silently diverge from the server in either direction.
 */
export const KnownSessionEventPayloadFields = {
  SessionCreated: ["SessionId", "Status"],
  SessionStarted: ["SessionId", "Status"],
  SessionEnded: ["SessionId", "Status"],
  ParticipantJoined: ["ParticipantId"],
  ParticipantLeft: ["ParticipantId"],
  SceneActivated: ["SceneId"],
  VisibilityRuleChanged: ["ResourceType", "ResourceId", "Visibility"],
  ContentRevealed: ["ResourceType", "ResourceId"],
  ContentHidden: ["ResourceType", "ResourceId"],
  RecapGenerated: ["RecapId", "SessionId"],
} as const satisfies {
  [K in KnownSessionEventType]: readonly (keyof SessionEventPayloadMap[K])[];
};
