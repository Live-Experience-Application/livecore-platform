// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

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
  AuditLogPageResponse,
  ContentBlockResponse,
  CurrentPrincipalResponse,
  EntityResponse,
  EntityTypeResponse,
  ExportArtifactResponse,
  HideResponse,
  LiveSessionEvent,
  MeEntitlementsResponse,
  OrganizationResponse,
  PageResponse,
  ParticipantContentBlockResponse,
  ParticipantEntityResponse,
  ParticipantPresenceResponse,
  ParticipantSceneResponse,
  PendingWorkspaceInvitationResponse,
  PersonalDataExportResponse,
  ProblemDetails,
  PurchaseVerificationResponse,
  RecapSummaryView,
  RecapView,
  RevealResponse,
  SceneResponse,
  SessionEventReplayResponse,
  SessionHubConnectionParams,
  SessionResponse,
  TemplateResponse,
  UploadIntentResponse,
  WorkspaceMemberResponse,
  WorkspaceResponse,
} from "@livecore/contracts";

import type {
  AssetsClient,
  AuditClient,
  ConditionalWriteOptions,
  ContentClient,
  EntitiesClient,
  EntityTypesClient,
  EntitlementsClient,
  ExportsClient,
  HideOptions,
  HubConnectionFactory,
  IdentityClient,
  LiveCoreApiError,
  LiveCoreClient,
  LiveCoreClientOptions,
  LiveSessionConnection,
  OrganizationsClient,
  RateLimitInfo,
  RealtimeClient,
  RecapsClient,
  RevealOptions,
  ScenesClient,
  SdkResponse,
  SessionsClient,
  StoreClient,
  TemplatesClient,
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

// The workspace list is a bounded page envelope (CORE-DX-003), never an unbounded array.
export type ListWorkspacesReturn = Assert<
  Equal<
    Awaited<ReturnType<WorkspacesClient["list"]>>,
    PageResponse<WorkspaceResponse>
  >
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

// --- CORE-RT-007: the live realtime client connects with identifiers and surfaces a
// typed event stream. The connect parameters are the connection IDENTIFIERS shape
// (never a group name), the event handler receives the typed `LiveSessionEvent`
// envelope, and a successful connect resolves to a `LiveSessionConnection` handle.

export type ConnectParamsAreConnectionIdentifiers = Assert<
  Equal<Parameters<RealtimeClient["connect"]>[0], SessionHubConnectionParams>
>;

export type ConnectHandlerReceivesTypedEvent = Assert<
  Equal<
    Parameters<RealtimeClient["connect"]>[1],
    (event: LiveSessionEvent) => void
  >
>;

export type ConnectReturn = Assert<
  Equal<Awaited<ReturnType<RealtimeClient["connect"]>>, LiveSessionConnection>
>;

export type StoreReturn = Assert<
  Equal<
    Awaited<ReturnType<StoreClient["verifyAppleTransaction"]>>,
    PurchaseVerificationResponse
  >
>;

// --- The scene list is the role-projected union of bounded pages (CORE-DX-003). ---

export type SceneListReturn = Assert<
  Equal<
    Awaited<ReturnType<ScenesClient["list"]>>,
    PageResponse<SceneResponse> | PageResponse<ParticipantSceneResponse>
  >
>;

// --- The entity list is the role-projected union of bounded pages; create is host. ---

export type EntityListReturn = Assert<
  Equal<
    Awaited<ReturnType<EntitiesClient["list"]>>,
    PageResponse<EntityResponse> | PageResponse<ParticipantEntityResponse>
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
    | "hubConnectionFactory"
  >
>;

// The live realtime hub connection factory is optional (the REST routes never need
// it; only `realtime.connect` does), so omitting it is valid.
export type HubConnectionFactoryIsOptional = Assert<
  Equal<
    LiveCoreClientOptions["hubConnectionFactory"],
    HubConnectionFactory | undefined
  >
>;

// --- The API error carries a numeric status and an optional Problem Details. -----

export type ApiErrorStatus = Assert<Equal<LiveCoreApiError["status"], number>>;

export type ApiErrorProblem = Assert<
  Equal<LiveCoreApiError["problem"], ProblemDetails | undefined>
>;

// --- The API error surfaces the rate-limit signals instead of discarding them (CORE-DX-005). ---

export type ApiErrorRetryAfter = Assert<
  Equal<LiveCoreApiError["retryAfter"], number | undefined>
>;

export type ApiErrorRateLimit = Assert<
  Equal<LiveCoreApiError["rateLimit"], RateLimitInfo | undefined>
>;

export type RateLimitInfoShape = Assert<
  Equal<
    RateLimitInfo,
    {
      readonly limit?: number;
      readonly remaining?: number;
      readonly reset?: number;
    }
  >
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

// --- CORE-SDK-006: the completed SDK covers every implemented v1 route. ----------
// Each new method returns the EXACT contract response type (or `void` for a `204`
// delete), and the client exposes the five new resource groups as typed properties.

// New resource groups on the client.
export type ClientHasIdentity = Assert<
  Equal<LiveCoreClient["identity"], IdentityClient>
>;
export type ClientHasOrganizations = Assert<
  Equal<LiveCoreClient["organizations"], OrganizationsClient>
>;
export type ClientHasAudit = Assert<
  Equal<LiveCoreClient["audit"], AuditClient>
>;
export type ClientHasTemplates = Assert<
  Equal<LiveCoreClient["templates"], TemplatesClient>
>;
export type ClientHasRecaps = Assert<
  Equal<LiveCoreClient["recaps"], RecapsClient>
>;
export type ClientHasContent = Assert<
  Equal<LiveCoreClient["content"], ContentClient>
>;

// Identity: the principal-context read.
export type CurrentPrincipalReturn = Assert<
  Equal<
    Awaited<ReturnType<IdentityClient["getCurrentPrincipal"]>>,
    CurrentPrincipalResponse
  >
>;

// Organizations: list/create plus the offboarding/data-protection routes; a `204`
// delete/erase is `void`, the personal-data export is the DSAR response.
export type OrganizationsListReturn = Assert<
  Equal<
    Awaited<ReturnType<OrganizationsClient["list"]>>,
    OrganizationResponse[]
  >
>;
export type OrganizationsCreateReturn = Assert<
  Equal<
    Awaited<ReturnType<OrganizationsClient["create"]>>,
    OrganizationResponse
  >
>;
export type OrganizationsDeleteReturn = Assert<
  Equal<Awaited<ReturnType<OrganizationsClient["delete"]>>, void>
>;
export type OrganizationsEraseReturn = Assert<
  Equal<
    Awaited<ReturnType<OrganizationsClient["eraseMemberPersonalData"]>>,
    void
  >
>;
export type OrganizationsExportReturn = Assert<
  Equal<
    Awaited<ReturnType<OrganizationsClient["exportMemberPersonalData"]>>,
    PersonalDataExportResponse
  >
>;

// Audit: the tenant's append-only audit log page.
export type AuditListReturn = Assert<
  Equal<Awaited<ReturnType<AuditClient["list"]>>, AuditLogPageResponse>
>;

// Templates: list/read/create plus a `204` delete.
export type TemplatesListReturn = Assert<
  Equal<Awaited<ReturnType<TemplatesClient["list"]>>, TemplateResponse[]>
>;
export type TemplatesGetReturn = Assert<
  Equal<Awaited<ReturnType<TemplatesClient["get"]>>, TemplateResponse>
>;
export type TemplatesDeleteReturn = Assert<
  Equal<Awaited<ReturnType<TemplatesClient["delete"]>>, void>
>;

// Recaps: the role-projected recap read is the union of the full and summary views.
export type RecapReturn = Assert<
  Equal<
    Awaited<ReturnType<RecapsClient["getSessionRecap"]>>,
    RecapView | RecapSummaryView
  >
>;

// Workspaces: archive returns the workspace; pending-invitations is a bounded page;
// accept returns the new membership; revoke/remove are `204` voids.
export type ArchiveReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["archive"]>>, WorkspaceResponse>
>;
export type ListInvitationsReturn = Assert<
  Equal<
    Awaited<ReturnType<WorkspacesClient["listInvitations"]>>,
    PageResponse<PendingWorkspaceInvitationResponse>
  >
>;
export type AcceptInvitationReturn = Assert<
  Equal<
    Awaited<ReturnType<WorkspacesClient["acceptInvitation"]>>,
    WorkspaceMemberResponse
  >
>;
export type RemoveWorkspaceMemberReturn = Assert<
  Equal<Awaited<ReturnType<WorkspacesClient["removeMember"]>>, void>
>;

// Sessions: list is a bounded page; create/get return a session; presence commands
// return the identifier-only presence response.
export type SessionListReturn = Assert<
  Equal<
    Awaited<ReturnType<SessionsClient["list"]>>,
    PageResponse<SessionResponse>
  >
>;
export type SessionGetReturn = Assert<
  Equal<Awaited<ReturnType<SessionsClient["get"]>>, SessionResponse>
>;
export type JoinParticipantReturn = Assert<
  Equal<
    Awaited<ReturnType<SessionsClient["joinParticipant"]>>,
    ParticipantPresenceResponse
  >
>;
export type LeaveParticipantReturn = Assert<
  Equal<
    Awaited<ReturnType<SessionsClient["leaveParticipant"]>>,
    ParticipantPresenceResponse
  >
>;

// Scenes: the by-id read is the role-projected union; reorder returns the re-packed
// host list; delete is a `204` void.
export type SceneGetReturn = Assert<
  Equal<
    Awaited<ReturnType<ScenesClient["get"]>>,
    SceneResponse | ParticipantSceneResponse
  >
>;
export type SceneReorderReturn = Assert<
  Equal<Awaited<ReturnType<ScenesClient["reorder"]>>, SceneResponse[]>
>;
export type SceneDeleteReturn = Assert<
  Equal<Awaited<ReturnType<ScenesClient["delete"]>>, void>
>;

// Content: list/read are the role-projected unions; delete is a `204` void.
export type ContentListReturn = Assert<
  Equal<
    Awaited<ReturnType<ContentClient["listBlocks"]>>,
    | PageResponse<ContentBlockResponse>
    | PageResponse<ParticipantContentBlockResponse>
  >
>;
export type ContentGetReturn = Assert<
  Equal<
    Awaited<ReturnType<ContentClient["getBlock"]>>,
    ContentBlockResponse | ParticipantContentBlockResponse
  >
>;
export type ContentDeleteReturn = Assert<
  Equal<Awaited<ReturnType<ContentClient["deleteBlock"]>>, void>
>;

// Entities: the entity delete and the entity-relationship delete are `204` voids.
export type EntityDeleteReturn = Assert<
  Equal<Awaited<ReturnType<EntitiesClient["delete"]>>, void>
>;
export type EntityRelationshipDeleteReturn = Assert<
  Equal<Awaited<ReturnType<EntitiesClient["deleteRelationship"]>>, void>
>;

// Visibility: hide returns the hide response and REQUIRES a stable idempotency key
// (retry safety), exactly like reveal.
export type HideReturn = Assert<
  Equal<Awaited<ReturnType<VisibilityClient["hide"]>>, HideResponse>
>;
export type HideRequiresKey = Assert<
  Equal<Parameters<VisibilityClient["hide"]>[2], HideOptions>
>;
export type HideKeyIsRequired = Assert<
  Equal<HideOptions, { idempotencyKey: string }>
>;

// Assets: the link delete and the asset delete are `204` voids.
export type AssetDeleteLinkReturn = Assert<
  Equal<Awaited<ReturnType<AssetsClient["deleteLink"]>>, void>
>;
export type AssetDeleteReturn = Assert<
  Equal<Awaited<ReturnType<AssetsClient["delete"]>>, void>
>;

// Entitlements: the effective-entitlements read.
export type MyEntitlementsReturn = Assert<
  Equal<
    Awaited<ReturnType<EntitlementsClient["getMyEntitlements"]>>,
    MeEntitlementsResponse
  >
>;
