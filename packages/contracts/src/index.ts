/**
 * @livecore/contracts
 *
 * Stable, product-neutral TypeScript contract types for the LiveCore Core API
 * (CORE-SDK-001): the request/response DTOs, the generic enumerations, the
 * transport constants and the realtime event vocabulary a vertical app consumes.
 * Every type mirrors a Core API contract exactly — JSON over HTTPS with camelCase
 * fields, enums as stable string names, and RFC 7807 Problem Details for errors
 * (docs/08_API_CONTRACTS.md). The typed SDK client that calls these contracts is
 * a separate package (@livecore/sdk-ts, CORE-SDK-002).
 *
 * The package is product-neutral: it carries only generic Core vocabulary and no
 * vertical domain language (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md). A vertical
 * maps these generic shapes to its own UI labels.
 */

/** The npm package name, exported as a stable runtime value. */
export const PACKAGE_NAME = "@livecore/contracts";

/**
 * The package version, exported as a stable runtime value so a vertical app can
 * introspect which Core package release it is running against. The Core SDK and
 * UI packages are versioned together (lockstep) and follow Semantic Versioning;
 * this literal is kept in lockstep with `package.json` (a package-build test
 * fails if they drift) and with the top entry of this package's `CHANGELOG.md`.
 * See the versioning and changelog process in `docs/23_PACKAGE_VERSIONING.md`.
 */
export const VERSION = "0.1.0";

export * from "./scalars.js";
export * from "./http.js";
export * from "./pagination.js";
export * from "./problem-details.js";
export * from "./enums.js";
export * from "./events.js";
export * from "./workspaces.js";
export * from "./sessions.js";
export * from "./scenes.js";
export * from "./content.js";
export * from "./entities.js";
export * from "./entity-types.js";
export * from "./visibility.js";
export * from "./realtime.js";
export * from "./assets.js";
export * from "./exports.js";
export * from "./entitlements.js";
export * from "./store.js";
