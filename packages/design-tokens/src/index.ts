/**
 * @livecore/design-tokens
 *
 * Generic, product-neutral design tokens and theme contracts for LiveCore
 * vertical apps (CORE-SDK-003). Core owns the *contract* — the categories of
 * tokens (color, spacing, typography, radius, shadow, breakpoint, motion) and
 * the stable generic keys within each (see `scales.ts`) — plus a neutral
 * `baseTheme` default. A vertical app owns the concrete values: it supplies its
 * own brand palette and type stack by defining a `Theme` that satisfies the
 * contract (themes are a vertical extension mechanism — AGENTS.md;
 * docs/04_PRODUCT_BOUNDARIES.md).
 *
 * The package is types-first and adds no runtime dependencies. The stable
 * runtime surface is the scale-key tuples (for enumeration/validation), the
 * `baseTheme` value and the `defineTheme` authoring helper; everything else is
 * compile-time types, mirroring how `@livecore/contracts` exposes its enums as
 * `as const` tuples alongside their unions.
 */

/** The npm package name, exported as a stable runtime value. */
export const PACKAGE_NAME = "@livecore/design-tokens";

export * from "./scales.js";
export * from "./tokens.js";
export * from "./base-theme.js";
