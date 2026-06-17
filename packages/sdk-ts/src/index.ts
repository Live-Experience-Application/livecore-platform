/**
 * @livecore/sdk-ts
 *
 * The typed TypeScript client for vertical apps built on the LiveCore Core API
 * (CORE-SDK-002). It wraps the implemented `/api/v1` routes with methods that
 * return the exact `@livecore/contracts` response types, attaches the OIDC
 * bearer token to every request, and turns a non-success response into a typed
 * {@link LiveCoreApiError}. Authorization stays server-side; the SDK is a typed
 * transport, never a security boundary (docs/07_SECURITY_THREAT_MODEL.md).
 *
 * The package is product-neutral: it carries only generic Core vocabulary and no
 * vertical domain language (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md). The DTO,
 * enum and event TYPES the methods accept and return live in the separate
 * `@livecore/contracts` package; import those directly when you need to name a
 * shape.
 */

/** The npm package name, exported as a stable runtime value. */
export const PACKAGE_NAME = "@livecore/sdk-ts";

/**
 * The package version, exported as a stable runtime value so a vertical app can
 * introspect which Core package release it is running against. The Core SDK and
 * UI packages are versioned together (lockstep) and follow Semantic Versioning;
 * this literal is kept in lockstep with `package.json` (a package-build test
 * fails if they drift) and with the top entry of this package's `CHANGELOG.md`.
 * See the versioning and changelog process in `docs/23_PACKAGE_VERSIONING.md`.
 */
export const VERSION = "0.1.0";

export { LiveCoreClient } from "./client.js";
export {
  LiveCoreError,
  LiveCoreApiError,
  isLiveCoreApiError,
} from "./errors.js";
export type { ApiErrorDetails, RateLimitInfo } from "./errors.js";
export type {
  AccessTokenProvider,
  FetchLike,
  FetchRequestInit,
  FetchResponse,
  LiveCoreClientOptions,
} from "./options.js";
export type { SdkResponse } from "./http.js";
export type { PageParams } from "./resources/pagination.js";

export { IdentityClient } from "./resources/identity.js";
export { OrganizationsClient } from "./resources/organizations.js";
export { AuditClient } from "./resources/audit.js";
export { TemplatesClient } from "./resources/templates.js";
export { WorkspacesClient } from "./resources/workspaces.js";
export type { ConditionalWriteOptions } from "./resources/workspaces.js";
export { SessionsClient } from "./resources/sessions.js";
export { ScenesClient } from "./resources/scenes.js";
export { ContentClient } from "./resources/content.js";
export { EntitiesClient } from "./resources/entities.js";
export { EntityTypesClient } from "./resources/entity-types.js";
export { VisibilityClient } from "./resources/visibility.js";
export type { HideOptions, RevealOptions } from "./resources/visibility.js";
export { RealtimeClient } from "./resources/realtime.js";
export type { SessionEventReplayParams } from "./resources/realtime.js";
export { RecapsClient } from "./resources/recaps.js";
export { AssetsClient } from "./resources/assets.js";
export { ExportsClient } from "./resources/exports.js";
export { EntitlementsClient } from "./resources/entitlements.js";
export { StoreClient } from "./resources/store.js";
