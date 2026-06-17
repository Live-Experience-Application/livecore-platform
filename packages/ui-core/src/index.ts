// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * @livecore/ui-core
 *
 * Generic, product-neutral UI primitive contracts for LiveCore vertical apps
 * (CORE-SDK-004). Core owns the *contract* — the variant vocabularies a
 * primitive's props are drawn from (`variants.ts`), the typed prop shape of each
 * generic primitive (`props.ts`) and the pure variant-resolution defaults
 * (`resolve.ts`). A vertical owns the *rendering*: it implements real components
 * (typically React, but the contract is framework-agnostic) that accept these
 * props and apply their theme (@livecore/design-tokens). Vertical-specific
 * screens and UI wrappers are a vertical extension mechanism
 * (docs/04_PRODUCT_BOUNDARIES.md); Core defines no screen.
 *
 * The package is types-first and adds no runtime dependencies. Its stable
 * runtime surface is the variant tuples (for enumeration/validation), the
 * default constants and the `resolveVariant` helper; everything else is
 * compile-time types, mirroring how the sibling Core packages expose their
 * vocabularies as `as const` tuples alongside their unions. It carries only
 * generic UI vocabulary and no vertical domain language (AGENTS.md).
 */

/** The npm package name, exported as a stable runtime value. */
export const PACKAGE_NAME = "@livecore/ui-core";

/**
 * The package version, exported as a stable runtime value so a vertical app can
 * introspect which Core package release it is running against. The Core SDK and
 * UI packages are versioned together (lockstep) and follow Semantic Versioning;
 * this literal is kept in lockstep with `package.json` (a package-build test
 * fails if they drift) and with the top entry of this package's `CHANGELOG.md`.
 * See the versioning and changelog process in `docs/23_PACKAGE_VERSIONING.md`.
 */
export const VERSION = "0.1.0";

export * from "./variants.js";
export * from "./props.js";
export * from "./resolve.js";
