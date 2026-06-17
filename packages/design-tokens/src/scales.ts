// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Stable, product-neutral design-token scale keys (CORE-SDK-003).
 *
 * Each scale is the set of generic names a theme indexes its values by — the
 * color roles, the spacing steps, the typographic scale, and so on. Like the
 * Core API enums (@livecore/contracts), every scale is exported as a runtime
 * `as const` tuple of its legal keys alongside its string-literal union, so a
 * vertical app can both type a token key and enumerate the keys at runtime (for
 * example to render a token reference table or validate a supplied theme).
 *
 * These names are generic UI vocabulary only; they carry no vertical visual
 * identity (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md — themes are a vertical
 * extension mechanism). A vertical maps a Core token role to its own brand
 * palette; Core owns the contract, not the brand.
 *
 * The keys carry no ordering meaning and must not be compared as a magnitude
 * ladder: a scale is a named lookup, not an ordered index.
 */

/** Color scheme a theme provides a full color set for (light and dark). */
export const ColorSchemes = ["light", "dark"] as const;

/** A color scheme name. */
export type ColorScheme = (typeof ColorSchemes)[number];

/**
 * Generic semantic color roles a theme assigns a value to in each scheme. The
 * roles describe a UI purpose (surface, foreground, primary action, status),
 * never a literal hue, so a vertical can re-skin every role without changing
 * any consuming component.
 */
export const ColorRoles = [
  "background",
  "surface",
  "overlay",
  "foreground",
  "muted",
  "border",
  "primary",
  "primaryForeground",
  "secondary",
  "secondaryForeground",
  "accent",
  "accentForeground",
  "success",
  "warning",
  "danger",
  "info",
] as const;

/** A semantic color role name. */
export type ColorRole = (typeof ColorRoles)[number];

/** Generic spacing steps (a t-shirt scale; `none` is the zero step). */
export const SpacingSteps = [
  "none",
  "xs",
  "sm",
  "md",
  "lg",
  "xl",
  "2xl",
  "3xl",
] as const;

/** A spacing step name. */
export type SpacingStep = (typeof SpacingSteps)[number];

/** Generic font-family roles a theme provides a stack for. */
export const FontFamilyRoles = ["sans", "serif", "mono"] as const;

/** A font-family role name. */
export type FontFamilyRole = (typeof FontFamilyRoles)[number];

/** Generic font-size steps (a t-shirt scale). */
export const FontSizeSteps = [
  "xs",
  "sm",
  "md",
  "lg",
  "xl",
  "2xl",
  "3xl",
] as const;

/** A font-size step name. */
export type FontSizeStep = (typeof FontSizeSteps)[number];

/** Generic font-weight roles a theme maps to a numeric weight. */
export const FontWeightRoles = [
  "regular",
  "medium",
  "semibold",
  "bold",
] as const;

/** A font-weight role name. */
export type FontWeightRole = (typeof FontWeightRoles)[number];

/** Generic line-height roles a theme maps to a unitless multiplier. */
export const LineHeightRoles = ["tight", "normal", "relaxed"] as const;

/** A line-height role name. */
export type LineHeightRole = (typeof LineHeightRoles)[number];

/** Generic border-radius steps (`none` is square; `full` is a pill/circle). */
export const RadiusSteps = ["none", "sm", "md", "lg", "full"] as const;

/** A border-radius step name. */
export type RadiusStep = (typeof RadiusSteps)[number];

/** Generic elevation/shadow steps (`none` is flat). */
export const ShadowSteps = ["none", "sm", "md", "lg"] as const;

/** A shadow elevation step name. */
export type ShadowStep = (typeof ShadowSteps)[number];

/** Generic responsive breakpoint steps (min-width thresholds). */
export const BreakpointSteps = ["sm", "md", "lg", "xl"] as const;

/** A breakpoint step name. */
export type BreakpointStep = (typeof BreakpointSteps)[number];

/** Generic motion duration steps (`instant` is zero-time). */
export const MotionDurationSteps = [
  "instant",
  "fast",
  "normal",
  "slow",
] as const;

/** A motion duration step name. */
export type MotionDurationStep = (typeof MotionDurationSteps)[number];

/** Generic motion easing roles a theme maps to a timing function. */
export const MotionEasingRoles = [
  "standard",
  "accelerate",
  "decelerate",
] as const;

/** A motion easing role name. */
export type MotionEasingRole = (typeof MotionEasingRoles)[number];
