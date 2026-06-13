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
  EntitlementsClient,
  LiveCoreApiError,
  LiveCoreClient,
  LiveCoreClientOptions,
  RealtimeClient,
  RevealOptions,
  ScenesClient,
  SessionsClient,
  StoreClient,
  VisibilityClient,
  WorkspacesClient,
} from "../src/index.js";

/** `true` only when `X` and `Y` are the exact same type. */
type Equal<X, Y> =
  (<T>() => T extends X ? 1 : 2) extends <T>() => T extends Y ? 1 : 2
    ? true
    : false;

/** Compiles only when its argument resolves to the literal `true`. */
type Assert<T extends true> = T;

// --- Each method returns the EXACT contract response type. ----------------------

export type ListWorkspacesReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["list"]>>, WorkspaceResponse[]>
>;

export type CreateWorkspaceReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["create"]>>, WorkspaceResponse>
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
