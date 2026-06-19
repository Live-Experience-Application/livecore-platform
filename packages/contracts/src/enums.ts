// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Generic, product-neutral enumerations the Core API emits and accepts
 * (CORE-SDK-001), each as a stable string NAME on the wire — the server persists
 * and serializes every enum by name, never by a numeric value. For each enum a
 * runtime `as const` tuple of the legal names is exported alongside its
 * string-literal union, so a vertical app can both type a value and validate one
 * at runtime (for example to populate a select control).
 *
 * These names are Core vocabulary only; a vertical maps them to its own UI
 * labels (docs/03_DOMAIN_LANGUAGE.md, docs/04_PRODUCT_BOUNDARIES.md). The integer
 * discriminators the server uses internally are never on the wire and never
 * appear here. The names carry no ordering meaning and must not be compared as
 * a privilege ladder (the Core authorization matrix is non-linear).
 */

/**
 * Generic role a subject holds in an organization or workspace, taken verbatim
 * from the Core authorization matrix (docs/06_AUTHORIZATION_MATRIX.md).
 */
export const MembershipRoles = [
  "Owner",
  "Admin",
  "Host",
  "CoHost",
  "Participant",
  "Observer",
  "Auditor",
] as const;

/** A generic Core membership role name. */
export type MembershipRole = (typeof MembershipRoles)[number];

/**
 * Lifecycle status of a workspace (`Active` until an owner archives it). An
 * archived workspace is read-only and excluded from the active workspace list
 * (CORE-LIFE-009).
 */
export const WorkspaceStatuses = ["Active", "Archived"] as const;

/** A workspace lifecycle status name. */
export type WorkspaceStatus = (typeof WorkspaceStatuses)[number];

/** Lifecycle status of a workspace member invitation. */
export const WorkspaceInvitationStatuses = [
  "Pending",
  "Accepted",
  "Revoked",
] as const;

/** A workspace invitation lifecycle status name. */
export type WorkspaceInvitationStatus =
  (typeof WorkspaceInvitationStatuses)[number];

/**
 * Lifecycle status of a participant (`Active` until a host removes it). A removed
 * participant is a soft delete retained for history and holds no standing.
 */
export const ParticipantStatuses = ["Active", "Removed"] as const;

/** A participant lifecycle status name. */
export type ParticipantStatus = (typeof ParticipantStatuses)[number];

/**
 * Outcome of a host-driven participant presence command. `Joined` admits a
 * participant, `Left` removes a present one (delivering `ParticipantLeft`), and
 * `AlreadyLeft` is the idempotent no-op for a participant that had already left.
 */
export const ParticipantPresenceOutcomes = [
  "Joined",
  "Left",
  "AlreadyLeft",
] as const;

/** A participant presence command outcome name. */
export type ParticipantPresenceOutcome =
  (typeof ParticipantPresenceOutcomes)[number];

/**
 * Lifecycle status of a session (`Prepared` → `Live` → `Ended`). A host may also
 * `cancel` a not-yet-started (`Prepared`) session, which moves it to the soft,
 * terminal `Cancelled` status instead of deleting it (CORE-LIFE-010).
 */
export const SessionStatuses = [
  "Prepared",
  "Live",
  "Ended",
  "Cancelled",
] as const;

/** A session lifecycle status name. */
export type SessionStatus = (typeof SessionStatuses)[number];

/** Generic kind of a content block (a text/media/data unit). */
export const ContentBlockTypes = ["Text", "Media", "Data"] as const;

/** A content block kind name. */
export type ContentBlockType = (typeof ContentBlockTypes)[number];

/** Generic kind of Core resource a visibility rule / reveal governs. */
export const VisibilityResourceTypes = [
  "Scene",
  "ContentBlock",
  "Entity",
] as const;

/** A visibility resource kind name. */
export type VisibilityResourceType = (typeof VisibilityResourceTypes)[number];

/**
 * The base audience visibility state a visibility rule assigns to its resource:
 * `Hidden` (host-only) until a host reveals it, then `Visible` to the audience.
 */
export const VisibilityStates = ["Hidden", "Visible"] as const;

/** A visibility state name. */
export type VisibilityState = (typeof VisibilityStates)[number];

/**
 * Whether a reveal command newly applied the change or recognized an idempotent
 * retry. Both outcomes leave the resource visible (the command is idempotent).
 */
export const RevealOutcomes = ["Applied", "AlreadyApplied"] as const;

/** A reveal command outcome name. */
export type RevealOutcome = (typeof RevealOutcomes)[number];

/**
 * Whether a hide command newly applied the change or recognized an idempotent
 * retry. Both outcomes leave the resource hidden (the command is idempotent); it
 * mirrors {@link RevealOutcome} with the opposite visibility sense.
 */
export const HideOutcomes = ["Applied", "AlreadyApplied"] as const;

/** A hide command outcome name. */
export type HideOutcome = (typeof HideOutcomes)[number];

/** Lifecycle status of an asset (`Pending` until its upload is confirmed). */
export const AssetStatuses = ["Pending", "Available"] as const;

/** An asset lifecycle status name. */
export type AssetStatus = (typeof AssetStatuses)[number];

/** Generic kind of Core resource an asset may be linked to. */
export const AssetLinkTargetTypes = ["ContentBlock", "Entity"] as const;

/** An asset-link target kind name. */
export type AssetLinkTargetType = (typeof AssetLinkTargetTypes)[number];

/**
 * The explicit, generic scope of an export job — what data set the export covers
 * (`Workspace` = the full workspace data set; `UserData` = a single user's own
 * data). An export scope is a security boundary, not a cosmetic label: it bounds
 * what an export may ever include and can never be widened after creation (the
 * "explicit host/admin export scopes" control for threat T8).
 */
export const ExportScopes = ["Workspace", "UserData"] as const;

/** An export scope name. */
export type ExportScope = (typeof ExportScopes)[number];

/**
 * The generic, product-neutral kind of Core resource a workspace export manifest
 * inventories. The manifest carries a count per kind, never the content itself,
 * so it reveals only the shape of an export (threats T7/T8).
 */
export const ExportResourceKinds = [
  "Session",
  "Scene",
  "ContentBlock",
  "Entity",
  "Participant",
  "Asset",
] as const;

/** An export resource kind name. */
export type ExportResourceKind = (typeof ExportResourceKinds)[number];

/** The unit a quota measures usage in. */
export const QuotaUnits = ["Count", "Bytes"] as const;

/** A quota unit name. */
export type QuotaUnit = (typeof QuotaUnits)[number];

/**
 * Whether an effective entitlement is a boolean capability flag or a numeric
 * quota cap (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md). A flag carries a
 * boolean value; a quota carries a numeric limit (or `null` for unlimited).
 */
export const EntitlementValueKinds = ["Flag", "Quota"] as const;

/** An entitlement value-kind name. */
export type EntitlementValueKind = (typeof EntitlementValueKinds)[number];

/**
 * The external store infrastructure provider that verifies a purchase. These are
 * infrastructure provider names, not vertical product vocabulary
 * (docs/21_ENTITLEMENTS_QUOTAS_AND_STORE_RECEIPTS.md,
 * docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md).
 */
export const PurchaseProviders = ["Apple", "Google"] as const;

/** A purchase provider name. */
export type PurchaseProvider = (typeof PurchaseProviders)[number];

/** Lifecycle status of a verified purchase transaction. */
export const PurchaseTransactionStatuses = [
  "Active",
  "Cancelled",
  "Refunded",
  "InGracePeriod",
] as const;

/** A purchase transaction status name. */
export type PurchaseTransactionStatus =
  (typeof PurchaseTransactionStatuses)[number];

/**
 * What a store-notification endpoint did with a received notification. The
 * endpoint acknowledges every authentic notification it has safely accounted for
 * so the store stops re-delivering it (docs/21).
 */
export const StoreNotificationOutcomes = [
  "Applied",
  "Unchanged",
  "AlreadyProcessed",
  "TransactionNotFound",
  "Ignored",
] as const;

/** A store-notification processing outcome name. */
export type StoreNotificationOutcome =
  (typeof StoreNotificationOutcomes)[number];

/**
 * Stable wire code explaining an ad-eligibility decision
 * (docs/22_ADS_AND_MOBILE_BILLING_BOUNDARIES.md). A vertical maps the code to its
 * own paywall copy; Core never returns ad placements or provider configuration.
 */
export const AdEligibilityReasonCodes = [
  "NO_AD_FREE_ENTITLEMENT",
  "ADS_REQUIRED_ENTITLEMENT",
  "AD_FREE_ENTITLEMENT",
] as const;

/** An ad-eligibility reason wire code. */
export type AdEligibilityReasonCode = (typeof AdEligibilityReasonCodes)[number];
