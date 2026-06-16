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
  AdEligibilityResponse,
  AssetStatus,
  ContentBlockType,
  CreateWorkspaceRequest,
  MembershipRole,
  ProblemCode,
  ProblemDetails,
  PurchaseProvider,
  RevealRequest,
  SessionResponse,
  SessionStatus,
  StoreNotificationAck,
  VisibilityResourceType,
  WorkspaceResponse,
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
