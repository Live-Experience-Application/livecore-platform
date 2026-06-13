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

export * from "./variants.js";
export * from "./props.js";
export * from "./resolve.js";
