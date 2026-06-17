// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The generic design-token contract (CORE-SDK-003): the typed shape of a theme
 * a vertical app supplies and a Core UI primitive consumes. Core owns this
 * contract — the categories of tokens and the keys within each category (see
 * `scales.ts`) — while a vertical owns the concrete values (its brand palette,
 * type stack and motion feel). The contract is product-neutral: it names UI
 * purposes only and carries no vertical visual identity (AGENTS.md;
 * docs/04_PRODUCT_BOUNDARIES.md — themes are a vertical extension mechanism).
 *
 * The value aliases are deliberately plain CSS strings/numbers (not branded
 * types), so a token value is exactly what a stylesheet or inline style consumes
 * with no runtime wrapping, mirroring how `@livecore/contracts` keeps its wire
 * scalars as plain strings.
 */
import type {
  BreakpointStep,
  ColorRole,
  ColorScheme,
  FontFamilyRole,
  FontSizeStep,
  FontWeightRole,
  LineHeightRole,
  MotionDurationStep,
  MotionEasingRole,
  RadiusStep,
  ShadowStep,
  SpacingStep,
} from "./scales.js";

/** A CSS color value, e.g. `"#0f172a"`, `"rgb(15 23 42)"` or `"transparent"`. */
export type ColorValue = string;

/** A CSS length value, e.g. `"1rem"`, `"16px"` or `"0"`. */
export type DimensionValue = string;

/** A CSS `font-family` stack, e.g. `'system-ui, "Segoe UI", sans-serif'`. */
export type FontFamilyValue = string;

/** A CSS numeric font weight, e.g. `400` or `700`. */
export type FontWeightValue = number;

/** A unitless CSS `line-height` multiplier, e.g. `1.5`. */
export type LineHeightValue = number;

/** A CSS `box-shadow` value, e.g. `"0 1px 2px 0 rgba(15, 23, 42, 0.08)"`. */
export type ShadowValue = string;

/** A CSS time value, e.g. `"200ms"` or `"0ms"`. */
export type DurationValue = string;

/** A CSS timing function, e.g. `"cubic-bezier(0.2, 0, 0, 1)"` or `"linear"`. */
export type EasingValue = string;

/** The color values for every semantic role within a single scheme. */
export type ColorRoleTokens = Readonly<Record<ColorRole, ColorValue>>;

/** The full color set: every role, for each supported scheme. */
export type ColorSchemeTokens = Readonly<Record<ColorScheme, ColorRoleTokens>>;

/** The spacing value for every step. */
export type SpacingTokens = Readonly<Record<SpacingStep, DimensionValue>>;

/** The font-family stack for every role. */
export type FontFamilyTokens = Readonly<
  Record<FontFamilyRole, FontFamilyValue>
>;

/** The font-size value for every step. */
export type FontSizeTokens = Readonly<Record<FontSizeStep, DimensionValue>>;

/** The numeric weight for every font-weight role. */
export type FontWeightTokens = Readonly<
  Record<FontWeightRole, FontWeightValue>
>;

/** The unitless multiplier for every line-height role. */
export type LineHeightTokens = Readonly<
  Record<LineHeightRole, LineHeightValue>
>;

/** The radius value for every step. */
export type RadiusTokens = Readonly<Record<RadiusStep, DimensionValue>>;

/** The shadow value for every elevation step. */
export type ShadowTokens = Readonly<Record<ShadowStep, ShadowValue>>;

/** The min-width threshold for every breakpoint step. */
export type BreakpointTokens = Readonly<Record<BreakpointStep, DimensionValue>>;

/** The duration value for every motion step. */
export type MotionDurationTokens = Readonly<
  Record<MotionDurationStep, DurationValue>
>;

/** The timing function for every motion easing role. */
export type MotionEasingTokens = Readonly<
  Record<MotionEasingRole, EasingValue>
>;

/** The typographic tokens: families, sizes, weights and line heights. */
export interface TypographyTokens {
  readonly fontFamily: FontFamilyTokens;
  readonly fontSize: FontSizeTokens;
  readonly fontWeight: FontWeightTokens;
  readonly lineHeight: LineHeightTokens;
}

/** The motion tokens: durations and easing curves. */
export interface MotionTokens {
  readonly duration: MotionDurationTokens;
  readonly easing: MotionEasingTokens;
}

/**
 * The complete set of design tokens that defines a theme's look — one value for
 * every key in every scale. A vertical's theme must populate all of them, which
 * the type system enforces at compile time so a partial theme cannot ship.
 */
export interface ThemeTokens {
  readonly color: ColorSchemeTokens;
  readonly spacing: SpacingTokens;
  readonly typography: TypographyTokens;
  readonly radius: RadiusTokens;
  readonly shadow: ShadowTokens;
  readonly breakpoint: BreakpointTokens;
  readonly motion: MotionTokens;
}

/**
 * A named theme: a stable identifier plus its complete token set. The `name` is
 * a generic identifier (e.g. a slug), not a brand label — a vertical chooses its
 * own name when it defines a theme with `defineTheme`.
 */
export interface Theme {
  readonly name: string;
  readonly tokens: ThemeTokens;
}

/**
 * Identity helper that returns its argument unchanged, used only for its type:
 * authoring a theme through `defineTheme` makes the compiler check the literal
 * against the `Theme` contract, so a missing color role, an extra key or a wrong
 * value type is a compile error at the definition site rather than a runtime
 * surprise in a consuming component. Adds no runtime behavior beyond returning
 * the value.
 */
export function defineTheme(theme: Theme): Theme {
  return theme;
}
