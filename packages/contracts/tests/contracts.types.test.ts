/**
 * Compile-time type tests for @livecore/contracts (CORE-SDK-001).
 *
 * These assertions are verified by `tsc` against `tsconfig.test.json`: a failed
 * assertion is a type error, so the contract package fails to type-check (and the
 * package `test` script fails). The file has no runtime behavior — it is types
 * only — so it is intentionally NOT a Node test file; the runtime/package-build
 * checks live in `contracts.build.test.mjs`.
 */
import type {
  AcceptWorkspaceInvitationRequest,
  AdEligibilityResponse,
  AppleTransactionVerificationRequest,
  AssetStatus,
  ContentBlockType,
  CreateAssetLinkRequest,
  CreateContentBlockRequest,
  CreateEntityRequest,
  CreateEntityTypeRequest,
  CreateOrganizationRequest,
  CreateSceneRequest,
  CreateSessionRequest,
  CreateTemplateRequest,
  CreateUploadIntentRequest,
  CreateWorkspaceRequest,
  GoogleTokenVerificationRequest,
  HideRequest,
  InviteWorkspaceMemberRequest,
  KnownSessionEventType,
  MembershipRole,
  PageResponse,
  ParsedSessionEvent,
  ProblemCode,
  ProblemDetails,
  PurchaseProvider,
  ReorderSceneRequest,
  RevealRequest,
  SessionEventPayloadMap,
  SessionResponse,
  SessionStatus,
  StoreNotificationAck,
  UpdateWorkspaceRequest,
  VisibilityResourceType,
  WorkspaceResponse,
} from "../src/index.js";
import { KnownSessionEventPayloadFields, VERSION } from "../src/index.js";
// The OpenAPI-derived component schemas, generated from openapi/livecore-v1.json by
// `pnpm --filter @livecore/contracts run generate` (CORE-OAS-002). The curated DTOs
// above are validated against these below.
import type { components } from "../src/openapi.js";

/** `true` only when `X` and `Y` are the exact same type. */
type Equal<X, Y> =
  (<T>() => T extends X ? 1 : 2) extends <T>() => T extends Y ? 1 : 2
    ? true
    : false;

/** Compiles only when its argument resolves to the literal `true`. */
type Assert<T extends true> = T;

// --- The exported VERSION is a stable, well-formed SemVer string literal. -------
// (CORE-SDK-005.) The version is part of the package's typed surface, so a
// consumer can rely on its shape at compile time; the runtime agreement between
// VERSION, package.json and CHANGELOG.md is checked by the package-build test.

/** `true` only when `V` is a literal type, not the widened `string`. */
type IsStringLiteral<V extends string> = string extends V ? false : true;

/** `true` only when `V` has the SemVer `MAJOR.MINOR.PATCH` core shape. */
type IsSemanticVersion<V extends string> =
  V extends `${number}.${number}.${number}` ? true : false;

export type VersionIsStringLiteral = Assert<IsStringLiteral<typeof VERSION>>;
export type VersionIsSemanticVersion = Assert<
  IsSemanticVersion<typeof VERSION>
>;

// --- Enum unions mirror the server's stable name set exactly. ------------------

export type MembershipRoleIsExact = Assert<
  Equal<
    MembershipRole,
    | "Owner"
    | "Admin"
    | "Host"
    | "CoHost"
    | "Participant"
    | "Observer"
    | "Auditor"
  >
>;

export type SessionStatusIsExact = Assert<
  Equal<SessionStatus, "Prepared" | "Live" | "Ended" | "Cancelled">
>;

export type ContentBlockTypeIsExact = Assert<
  Equal<ContentBlockType, "Text" | "Media" | "Data">
>;

export type VisibilityResourceTypeIsExact = Assert<
  Equal<VisibilityResourceType, "Scene" | "ContentBlock" | "Entity">
>;

export type AssetStatusIsExact = Assert<
  Equal<AssetStatus, "Pending" | "Available">
>;

export type PurchaseProviderIsExact = Assert<
  Equal<PurchaseProvider, "Apple" | "Google">
>;

export type ProblemCodeIsExact = Assert<
  Equal<
    ProblemCode,
    | "validation_error"
    | "authentication_required"
    | "permission_denied"
    | "not_found"
    | "conflict"
    | "duplicate_resource"
    | "quota_exceeded"
    | "workspace_archived"
    | "concurrency_conflict"
    | "precondition_failed"
    | "unprocessable_entity"
    | "payload_too_large"
    | "rate_limited"
    | "internal_error"
    | "service_unavailable"
  >
>;

// The Problem Details `code` carries the stable catalog union (CORE-DX-001).
export type ProblemDetailsCodeIsProblemCode = Assert<
  Equal<ProblemDetails["code"], ProblemCode | undefined>
>;

// --- Nullable / optional wire fields keep their nullability. -------------------

export type SessionStartedAtIsNullable = Assert<
  Equal<SessionResponse["startedAt"], string | null>
>;

export type RevealParticipantIsOptional = Assert<
  Equal<RevealRequest["participantId"], string | undefined>
>;

// --- Request DTOs require exactly the documented fields. -----------------------

export type CreateWorkspaceFields = Assert<
  Equal<keyof CreateWorkspaceRequest, "organizationSlug" | "slug" | "name">
>;

// --- Enum-typed fields are the union, not a free string. -----------------------

export type StoreAckOutcomeIsUnion = Assert<
  Equal<
    StoreNotificationAck["outcome"],
    | "Applied"
    | "Unchanged"
    | "AlreadyProcessed"
    | "TransactionNotFound"
    | "Ignored"
  >
>;

// --- The wire shapes are constructible as object literals (structural check). ---

export const workspaceExample: WorkspaceResponse = {
  id: "0190f1d4-9b6e-7c3a-8a1e-0c2b3d4e5f60",
  organizationId: "0190f1d4-9b6e-7c3a-8a1e-0c2b3d4e5f61",
  slug: "demo",
  name: "Demo",
  status: "Active",
  createdAt: "2026-06-13T00:00:00+00:00",
  updatedAt: "2026-06-13T00:00:00+00:00",
  version: "8147",
};

// The bounded-list page envelope (CORE-DX-003) wraps any item shape with the stable
// offset/limit/hasMore/items fields.
export const workspacePageExample: PageResponse<WorkspaceResponse> = {
  offset: 0,
  limit: 50,
  hasMore: true,
  items: [workspaceExample],
};

export const adEligibilityExample: AdEligibilityResponse = {
  adsRequired: true,
  reason: "NO_AD_FREE_ENTITLEMENT",
  sessionAdFreeUntil: null,
  hostedSessionAdFree: false,
};

export const problemDetailsExample: ProblemDetails = {
  type: "about:blank",
  title: "Not Found",
  status: 404,
  code: "not_found",
};

// --- The curated DTOs are validated against the OpenAPI-derived schemas. --------
// (CORE-OAS-002.) `packages/contracts/src/openapi.ts` is generated from the committed
// OpenAPI 3 document `openapi/livecore-v1.json`, which is itself generated from — and
// drift-gated against — the server's minimal-API route table (CORE-OAS-001). A
// byte-for-byte gate (`check:openapi`, run in the CI `typescript` job and the package
// build test) fails if `openapi.ts` is out of date with that document, so the
// generated surface tracks the server exactly.
//
// These assertions are the curated-surface half of the coupling: every hand-written
// request DTO must carry EXACTLY the property names of the matching generated schema,
// so an added/removed/renamed server request field fails this type test until the
// curated DTO (and the changelog) is updated. We compare the property-NAME sets
// rather than the full value types deliberately: the ASP.NET minimal-API OpenAPI
// generator marks every required reference-type property `nullable` (a generated
// `name` is `string | null`) and marks server-required some fields the curated DTO
// documents as optional, so a structural `Equal` would fail on that quirk rather than
// on real drift. The property-name set is the drift-meaningful, quirk-stable
// invariant; the byte-diff gate covers value-type/nullability/format changes.

type Schemas = components["schemas"];

/** `true` only when curated DTO `C` carries exactly the property names of schema `S`. */
type SameKeys<C, S> = Equal<keyof C, keyof S>;

export type CreateWorkspaceRequestMatchesSchema = Assert<
  SameKeys<CreateWorkspaceRequest, Schemas["CreateWorkspaceRequest"]>
>;
export type UpdateWorkspaceRequestMatchesSchema = Assert<
  SameKeys<UpdateWorkspaceRequest, Schemas["UpdateWorkspaceRequest"]>
>;
export type InviteWorkspaceMemberRequestMatchesSchema = Assert<
  SameKeys<
    InviteWorkspaceMemberRequest,
    Schemas["InviteWorkspaceMemberRequest"]
  >
>;
export type CreateSceneRequestMatchesSchema = Assert<
  SameKeys<CreateSceneRequest, Schemas["CreateSceneRequest"]>
>;
export type CreateContentBlockRequestMatchesSchema = Assert<
  SameKeys<CreateContentBlockRequest, Schemas["CreateContentBlockRequest"]>
>;
export type CreateEntityRequestMatchesSchema = Assert<
  SameKeys<CreateEntityRequest, Schemas["CreateEntityRequest"]>
>;
export type CreateEntityTypeRequestMatchesSchema = Assert<
  SameKeys<CreateEntityTypeRequest, Schemas["CreateEntityTypeRequest"]>
>;
export type RevealRequestMatchesSchema = Assert<
  SameKeys<RevealRequest, Schemas["RevealRequest"]>
>;
export type CreateUploadIntentRequestMatchesSchema = Assert<
  SameKeys<CreateUploadIntentRequest, Schemas["CreateUploadIntentRequest"]>
>;
export type CreateAssetLinkRequestMatchesSchema = Assert<
  SameKeys<CreateAssetLinkRequest, Schemas["CreateAssetLinkRequest"]>
>;
export type AppleTransactionVerificationRequestMatchesSchema = Assert<
  SameKeys<
    AppleTransactionVerificationRequest,
    Schemas["AppleTransactionVerificationRequest"]
  >
>;
export type GoogleTokenVerificationRequestMatchesSchema = Assert<
  SameKeys<
    GoogleTokenVerificationRequest,
    Schemas["GoogleTokenVerificationRequest"]
  >
>;
// The six request DTOs CORE-SDK-006 added a curated alias for, so the completed
// typed SDK can route them in terms of @livecore/contracts. Each must carry exactly
// the property names of its generated schema, same as the DTOs above.
export type AcceptWorkspaceInvitationRequestMatchesSchema = Assert<
  SameKeys<
    AcceptWorkspaceInvitationRequest,
    Schemas["AcceptWorkspaceInvitationRequest"]
  >
>;
export type CreateOrganizationRequestMatchesSchema = Assert<
  SameKeys<CreateOrganizationRequest, Schemas["CreateOrganizationRequest"]>
>;
export type CreateSessionRequestMatchesSchema = Assert<
  SameKeys<CreateSessionRequest, Schemas["CreateSessionRequest"]>
>;
export type CreateTemplateRequestMatchesSchema = Assert<
  SameKeys<CreateTemplateRequest, Schemas["CreateTemplateRequest"]>
>;
export type HideRequestMatchesSchema = Assert<
  SameKeys<HideRequest, Schemas["HideRequest"]>
>;
export type ReorderSceneRequestMatchesSchema = Assert<
  SameKeys<ReorderSceneRequest, Schemas["ReorderSceneRequest"]>
>;

// The generated ProblemDetails schema carries exactly the fields the curated
// `ProblemDetails` models. The curated type adds an index signature for the RFC 7807
// "readers must tolerate unknown members" rule, which widens its own `keyof`, so we
// pin the generated schema's field names directly instead of comparing key sets.
export type ProblemDetailsSchemaFieldsAreModeled = Assert<
  Equal<
    keyof Schemas["ProblemDetails"],
    "type" | "title" | "status" | "detail" | "instance" | "code"
  >
>;

// The generated surface covers EVERY request body the server's OpenAPI document
// declares. Each is now modeled with a curated alias and validated above (CORE-SDK-006
// added the last six so the typed SDK routes every request in terms of the curated
// contracts), and each is also reachable as `OpenApi.components["schemas"][...]`. Adding
// or removing a server request schema changes this set and fails the assertion (and the
// byte-diff gate), so the generated contract surface cannot silently drift from the server.
export type GeneratedSchemaSetIsExact = Assert<
  Equal<
    keyof Schemas,
    | "AcceptWorkspaceInvitationRequest"
    | "AppleTransactionVerificationRequest"
    | "CreateAssetLinkRequest"
    | "CreateContentBlockRequest"
    | "CreateEntityRequest"
    | "CreateEntityTypeRequest"
    | "CreateOrganizationRequest"
    | "CreateSceneRequest"
    | "CreateSessionRequest"
    | "CreateTemplateRequest"
    | "CreateUploadIntentRequest"
    | "CreateWorkspaceRequest"
    | "GoogleTokenVerificationRequest"
    | "HideRequest"
    | "InviteWorkspaceMemberRequest"
    | "ProblemDetails"
    | "ReorderSceneRequest"
    | "RevealRequest"
    | "UpdateWorkspaceRequest"
  >
>;

// --- The session-event vocabulary and its typed payloads (CORE-RT-008). ---------
// The known-event vocabulary is the ten emitted Core events; a contract/drift test
// (tests/contracts.events.test.mjs) binds this set and the payload field sets to
// csv/event_catalog.csv, SessionEventTypes.cs and SessionEventPayloads.cs, so this
// pins the published surface so it cannot change unnoticed.

export type KnownSessionEventTypeIsExact = Assert<
  Equal<
    KnownSessionEventType,
    | "SessionCreated"
    | "SessionStarted"
    | "SessionEnded"
    | "ParticipantJoined"
    | "ParticipantLeft"
    | "SceneActivated"
    | "VisibilityRuleChanged"
    | "ContentRevealed"
    | "ContentHidden"
    | "RecapGenerated"
  >
>;

// Every known event has exactly one payload contract — the payload map keys equal
// the vocabulary, so an event added/removed without a payload (or vice versa) fails.
export type PayloadMapCoversTheVocabulary = Assert<
  Equal<keyof SessionEventPayloadMap, KnownSessionEventType>
>;

// The runtime KnownSessionEventPayloadFields tuple for each event lists EXACTLY the
// field names of that event's payload type, so the runtime map the drift gate
// compares to the server cannot drift from the typed payload contracts. A single
// mismatch (a dropped, added or renamed field name) makes the per-event Equal
// `false`, so the aggregated union is no longer the literal `true`.
type PayloadFieldsRuntime = typeof KnownSessionEventPayloadFields;
type RuntimeFieldsMatchPayloadType<K extends KnownSessionEventType> = Equal<
  PayloadFieldsRuntime[K][number],
  keyof SessionEventPayloadMap[K]
>;
type EveryEventFieldsMatch = {
  [K in KnownSessionEventType]: RuntimeFieldsMatchPayloadType<K>;
}[KnownSessionEventType];
export type PayloadFieldsBindToPayloadTypes = Assert<
  Equal<EveryEventFieldsMatch, true>
>;

// A consumer can discriminate a parsed payload by event type: switching on
// `eventType` narrows `payload` to the exact per-event shape, and the `never`
// default makes the switch fail to compile if a known event is left unhandled. This
// is the type test that the discriminated union is usable end to end.
export function discriminateParsedSessionEvent(
  event: ParsedSessionEvent,
): string {
  switch (event.eventType) {
    case "SessionCreated":
    case "SessionStarted":
    case "SessionEnded":
      return event.payload.Status;
    case "ParticipantJoined":
    case "ParticipantLeft":
      return event.payload.ParticipantId;
    case "SceneActivated":
      return event.payload.SceneId;
    case "VisibilityRuleChanged":
      return event.payload.Visibility;
    case "ContentRevealed":
    case "ContentHidden":
      return event.payload.ResourceType;
    case "RecapGenerated":
      return event.payload.RecapId;
    default:
      return assertNeverSessionEvent(event);
  }
}

/** Compile-time exhaustiveness guard: a known event left unhandled is a type error. */
function assertNeverSessionEvent(event: never): never {
  throw new Error(`unhandled session event: ${JSON.stringify(event)}`);
}
