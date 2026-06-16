/**
 * Compile-time type tests for @livecore/sdk-ts (CORE-SDK-002).
 *
 * These assertions are verified by `tsc` against `tsconfig.test.json`: a failed
 * assertion is a type error, so the package fails to type-check (and the package
 * `test` script fails). The file has no runtime behavior — it is types only — so
 * it is intentionally NOT a Node test file; the runtime/transport checks live in
 * `sdk-ts.build.test.mjs`.
 */
import type {
  AdEligibilityResponse,
  EntityResponse,
  EntityTypeResponse,
  ExportArtifactResponse,
  ParticipantEntityResponse,
  ParticipantSceneResponse,
  ProblemDetails,
  PurchaseVerificationResponse,
  RevealResponse,
  SceneResponse,
  SessionEventReplayResponse,
  SessionResponse,
  UploadIntentResponse,
  WorkspaceResponse,
} from "@livecore/contracts";

import type {
  AssetsClient,
  ConditionalWriteOptions,
  EntitiesClient,
  EntityTypesClient,
  EntitlementsClient,
  ExportsClient,
  LiveCoreApiError,
  LiveCoreClient,
  LiveCoreClientOptions,
  RealtimeClient,
  RevealOptions,
  ScenesClient,
  SdkResponse,
  SessionsClient,
  StoreClient,
  VisibilityClient,
  WorkspacesClient,
} from "../src/index.js";
import { VERSION } from "../src/index.js";

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

// --- Each method returns the EXACT contract response type. ----------------------

export type ListWorkspacesReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["list"]>>, WorkspaceResponse[]>
>;

export type CreateWorkspaceReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["create"]>>, WorkspaceResponse>
>;

// --- The conditional-write surface round-trips the weak ETag (CORE-DX-002). ------
// getWithETag returns the body PLUS its ETag; update accepts an optional If-Match.

export type GetWithETagReturn = Assert<
  Equal<
    Awaited<ReturnType<WorkspacesClient["getWithETag"]>>,
    SdkResponse<WorkspaceResponse>
  >
>;

export type UpdateAcceptsIfMatch = Assert<
  Equal<
    Parameters<WorkspacesClient["update"]>[2],
    ConditionalWriteOptions | undefined
  >
>;

export type ConditionalWriteOptionsShape = Assert<
  Equal<ConditionalWriteOptions, { ifMatch?: string }>
>;

export type StartSessionReturn = Assert<
  Equal<Awaited<ReturnType<SessionsClient["start"]>>, SessionResponse>
>;

export type RevealReturn = Assert<
  Equal<Awaited<ReturnType<VisibilityClient["reveal"]>>, RevealResponse>
>;

export type UploadIntentReturn = Assert<
  Equal<
    Awaited<ReturnType<AssetsClient["createUploadIntent"]>>,
    UploadIntentResponse
  >
>;

export type AdEligibilityReturn = Assert<
  Equal<
    Awaited<ReturnType<EntitlementsClient["getMyAdEligibility"]>>,
    AdEligibilityResponse
  >
>;

export type GetExportReturn = Assert<
  Equal<Awaited<ReturnType<ExportsClient["getExport"]>>, ExportArtifactResponse>
>;

export type ReplayReturn = Assert<
  Equal<
    Awaited<ReturnType<RealtimeClient["getSessionEvents"]>>,
    SessionEventReplayResponse
  >
>;

export type StoreReturn = Assert<
  Equal<
    Awaited<ReturnType<StoreClient["verifyAppleTransaction"]>>,
    PurchaseVerificationResponse
  >
>;

// --- The scene list is the role-projected union, not a single shape. ------------

export type SceneListReturn = Assert<
  Equal<
    Awaited<ReturnType<ScenesClient["list"]>>,
    SceneResponse[] | ParticipantSceneResponse[]
  >
>;

// --- The entity list/read are the role-projected union; create is the host shape.

export type EntityListReturn = Assert<
  Equal<
    Awaited<ReturnType<EntitiesClient["list"]>>,
    EntityResponse[] | ParticipantEntityResponse[]
  >
>;

export type EntityGetReturn = Assert<
  Equal<
    Awaited<ReturnType<EntitiesClient["get"]>>,
    EntityResponse | ParticipantEntityResponse
  >
>;

export type CreateEntityReturn = Assert<
  Equal<Awaited<ReturnType<EntitiesClient["create"]>>, EntityResponse>
>;

// --- The entity-type list/read/create are the single (non-projected) shape. ------
// An entity type is an authoring/schema artifact, not audience content, so there is
// no host-vs-participant union — every authoring caller receives the same shape.

export type EntityTypeListReturn = Assert<
  Equal<Awaited<ReturnType<EntityTypesClient["list"]>>, EntityTypeResponse[]>
>;

export type EntityTypeGetReturn = Assert<
  Equal<Awaited<ReturnType<EntityTypesClient["get"]>>, EntityTypeResponse>
>;

export type CreateEntityTypeReturn = Assert<
  Equal<Awaited<ReturnType<EntityTypesClient["create"]>>, EntityTypeResponse>
>;

// --- The reveal command REQUIRES a stable idempotency key (retry safety). --------

export type RevealRequiresKey = Assert<
  Equal<Parameters<VisibilityClient["reveal"]>[2], RevealOptions>
>;

export type RevealKeyIsRequired = Assert<
  Equal<RevealOptions, { idempotencyKey: string }>
>;

// --- The client options surface is exactly the documented keys. -----------------

export type OptionsKeys = Assert<
  Equal<
    keyof LiveCoreClientOptions,
    | "baseUrl"
    | "getAccessToken"
    | "fetch"
    | "generateRequestId"
    | "defaultHeaders"
  >
>;

// --- The API error carries a numeric status and an optional Problem Details. -----

export type ApiErrorStatus = Assert<Equal<LiveCoreApiError["status"], number>>;

export type ApiErrorProblem = Assert<
  Equal<LiveCoreApiError["problem"], ProblemDetails | undefined>
>;

// --- The client exposes each resource group as a typed property. ----------------

export type ClientHasWorkspaces = Assert<
  Equal<LiveCoreClient["workspaces"], WorkspacesClient>
>;

export type ClientHasStore = Assert<
  Equal<LiveCoreClient["store"], StoreClient>
>;

export type ClientHasEntities = Assert<
  Equal<LiveCoreClient["entities"], EntitiesClient>
>;

export type ClientHasEntityTypes = Assert<
  Equal<LiveCoreClient["entityTypes"], EntityTypesClient>
>;
