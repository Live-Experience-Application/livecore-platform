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

/** Lifecycle status of a session (`Prepared` → `Live` → `Ended`). */
export const SessionStatuses = ["Prepared", "Live", "Ended"] as const;

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
 * Whether a reveal command newly applied the change or recognized an idempotent
 * retry. Both outcomes leave the resource visible (the command is idempotent).
 */
export const RevealOutcomes = ["Applied", "AlreadyApplied"] as const;

/** A reveal command outcome name. */
export type RevealOutcome = (typeof RevealOutcomes)[number];

/** Lifecycle status of an asset (`Pending` until its upload is confirmed). */
export const AssetStatuses = ["Pending", "Available"] as const;

/** An asset lifecycle status name. */
export type AssetStatus = (typeof AssetStatuses)[number];

/** Generic kind of Core resource an asset may be linked to. */
export const AssetLinkTargetTypes = ["ContentBlock", "Entity"] as const;

/** An asset-link target kind name. */
export type AssetLinkTargetType = (typeof AssetLinkTargetTypes)[number];

/** The unit a quota measures usage in. */
export const QuotaUnits = ["Count", "Bytes"] as const;

/** A quota unit name. */
export type QuotaUnit = (typeof QuotaUnits)[number];

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
