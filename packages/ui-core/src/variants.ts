// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * Stable, product-neutral UI primitive variant vocabularies (CORE-SDK-004).
 *
 * These are the generic names a Core UI primitive's props are drawn from — the
 * tones, sizes, emphases and layout options a vertical app selects when it
 * consumes a primitive. Like the Core API enums (@livecore/contracts) and the
 * design-token scales (@livecore/design-tokens), every vocabulary is exported
 * as a runtime `as const` tuple of its legal keys alongside its string-literal
 * union, so a vertical can both type a prop value and enumerate the options at
 * runtime (for example to render a variant picker or validate input).
 *
 * The vocabularies are generic UI purpose names only; they carry no vertical
 * visual identity and no vertical domain language (AGENTS.md;
 * docs/04_PRODUCT_BOUNDARIES.md — themes and vertical UI wrappers are a vertical
 * extension mechanism). A vertical maps a Core tone to its own themed color and
 * renders the actual element; Core owns the contract, not the brand or the
 * framework.
 *
 * The keys carry no ordering meaning and must not be compared as a magnitude
 * ladder: a vocabulary is a named set of options, not an ordered index.
 */

/**
 * Semantic tone (visual intent) a primitive can carry. The tones describe a UI
 * purpose (a neutral element, a primary action, a status), never a literal hue,
 * so a vertical can re-skin every tone through its theme without changing any
 * consuming component.
 */
export const Tones = [
  "neutral",
  "primary",
  "secondary",
  "accent",
  "success",
  "warning",
  "danger",
  "info",
] as const;

/** A semantic tone name. */
export type Tone = (typeof Tones)[number];

/** The default tone applied when a primitive does not specify one. */
export const DEFAULT_TONE: Tone = "neutral";

/** Generic size scale for a primitive (a t-shirt scale). */
export const Sizes = ["sm", "md", "lg"] as const;

/** A size name. */
export type Size = (typeof Sizes)[number];

/** The default size applied when a primitive does not specify one. */
export const DEFAULT_SIZE: Size = "md";

/**
 * How strongly a primitive expresses its tone (fill style). `solid` is a filled
 * surface, `soft` a tinted surface, `outline` a bordered transparent surface and
 * `ghost` an unbordered transparent surface.
 */
export const Emphases = ["solid", "soft", "outline", "ghost"] as const;

/** An emphasis name. */
export type Emphasis = (typeof Emphases)[number];

/** The default emphasis applied when a primitive does not specify one. */
export const DEFAULT_EMPHASIS: Emphasis = "solid";

/**
 * Elevation level of a surface relative to the page: `base` sits on the page,
 * `raised` is lifted (a card), `overlay` floats above (a dialog/menu).
 */
export const SurfaceLevels = ["base", "raised", "overlay"] as const;

/** A surface level name. */
export type SurfaceLevel = (typeof SurfaceLevels)[number];

/** The default surface level applied when a surface does not specify one. */
export const DEFAULT_SURFACE_LEVEL: SurfaceLevel = "base";

/** Layout axis a stack arranges its children along. */
export const Orientations = ["horizontal", "vertical"] as const;

/** An orientation name. */
export type Orientation = (typeof Orientations)[number];

/** Cross-axis alignment of a stack's children. */
export const Alignments = ["start", "center", "end", "stretch"] as const;

/** An alignment name. */
export type Alignment = (typeof Alignments)[number];

/** Main-axis distribution of a stack's children. */
export const Justifications = [
  "start",
  "center",
  "end",
  "between",
  "around",
] as const;

/** A justification name. */
export type Justification = (typeof Justifications)[number];

/**
 * The generic catalog of UI primitives Core defines a typed prop contract for
 * (see `props.ts`). These are framework-agnostic building blocks only — never a
 * screen and never a vertical concept. A vertical renders each primitive with
 * its own framework and composes them into its own screens
 * (docs/04_PRODUCT_BOUNDARIES.md — vertical-specific screens and UI wrappers are
 * a vertical extension mechanism).
 */
export const PrimitiveKinds = [
  "surface",
  "stack",
  "text",
  "heading",
  "button",
  "badge",
  "field",
  "spinner",
  "divider",
  "avatar",
] as const;

/** A primitive kind name. */
export type PrimitiveKind = (typeof PrimitiveKinds)[number];
